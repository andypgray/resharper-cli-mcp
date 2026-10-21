using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Formatting;

namespace Zphil.ReSharperCli.Services;

/// <summary>Represents a generation that could not be deleted, and what the filesystem said about it.</summary>
internal sealed record CacheResetFailure(string Name, string Reason);

/// <summary>
///     Defines how much a reset can say about which checkout a generation it left alone belongs to.
/// </summary>
/// <remarks>
///     The answers read differently to someone deciding what to do next, so each has a member of its own
///     rather than being folded into a nullable path and a flag beside it.
/// </remarks>
internal enum LeftAloneAttribution
{
    /// <summary>
    ///     No successful run through this server ever stamped a marker naming it — a run killed at the cap,
    ///     or a <c>jb</c> started outside this server's queue. Nothing here knows whose it is.
    /// </summary>
    NoRunOnRecord,

    /// <summary>
    ///     A marker names the generation but records no solution path: written by a build of this server that
    ///     does not record one, or under naming drift. The next clean run against it fixes that on its own.
    /// </summary>
    PathNotRecorded,

    /// <summary>
    ///     The marker records the solution path its last successful run was against, and a file is still
    ///     there: someone's working checkout.
    /// </summary>
    CheckoutPresent,

    /// <summary>
    ///     The marker records the solution path, and nothing is there any more: the reclaimable cache of a
    ///     checkout deleted with its cache still on disk.
    /// </summary>
    CheckoutGone
}

/// <summary>
///     Represents a generation the reset left where it was, and the checkout it belongs to.
/// </summary>
/// <remarks>
///     Left alone means the hash in its name is another solution path's, and the hash is one-way, so the
///     recorded path is the whole of what turns a directory the caller can see into one they can act on.
///     <paramref name="LastWarmedFor" /> is that path, as its last successful run recorded it, and is
///     <see langword="null" /> unless <paramref name="Attribution" /> is
///     <see cref="LeftAloneAttribution.CheckoutPresent" /> or <see cref="LeftAloneAttribution.CheckoutGone" />.
/// </remarks>
internal sealed record LeftAloneGeneration(
    string Name,
    LeftAloneAttribution Attribution,
    string? LastWarmedFor = null);

/// <summary>
///     Represents what a reset did: the generation directory names it dropped, the ones belonging to a
///     different solution it left where they were, and the ones it could not delete, under the cache home it
///     looked in.
/// </summary>
/// <remarks>
///     <paramref name="SolutionFileExists" /> is false for a reclaim, the cache of a worktree, clone or copy
///     that has been deleted. That changes what the report can promise, since there is no next run against
///     that path to be cold.
/// </remarks>
internal sealed record CacheResetOutcome(
    string SolutionPath,
    string CacheHome,
    IReadOnlyList<string> Dropped,
    IReadOnlyList<LeftAloneGeneration> LeftAlone,
    IReadOnlyList<CacheResetFailure> Failures,
    bool SolutionFileExists = true);

/// <summary>
///     Deletes the solution's ReSharper cache generations so the next <c>jb</c> run rebuilds its analysis
///     from cold — the invalidation the ReSharper CLI itself does not expose.
/// </summary>
/// <remarks>
///     <para>
///         The <see cref="JbRunLock" /> for the (solution, cache home) pair is held across the delete, so a
///         reset queues behind a run in flight instead of pulling the cache out from under it, and a run that
///         starts mid-delete queues behind the reset. <see cref="JbRunYield" /> is entered before the lock, so
///         a pre-warm holding the generation is stood down rather than waited out: the delete exists to throw
///         away what that pass is building. It takes no <see cref="JbRunSlot" />: the slot bounds <c>jb</c>
///         processes, and a reset spawns none.
///     </para>
///     <para>
///         A generation is deleted only where <see cref="JbCacheGenerations.FindFor" /> proves the hash in its
///         name is this solution path's; everything else is reported as left alone. The solution file itself
///         need not exist: a generation is addressed by the hash of the path, so a deleted checkout's cache can
///         still be dropped, which is a reclaim.
///     </para>
///     <para>
///         A failed delete is retried briefly and then reported rather than thrown, and may leave a
///         generation partly deleted: a directory <c>jb</c> rebuilds is a better outcome than a call that fails
///         after deleting most of one, and the tool is idempotent, so re-running finishes the job. The report
///         can give only what the filesystem said. The holder may be a <c>jb</c> in another session, or one this
///         server has just killed and not finished reaping, since this call cancels a speculative run rather
///         than waiting it out.
///     </para>
///     <para>
///         <paramref name="heartbeatInterval" /> sets how often a queued reset reports itself, defaulting to
///         <see cref="JbRunProgress.HeartbeatInterval" />. It is a parameter so a test waiting out more than one
///         beat need not pay the production interval for it.
///     </para>
/// </remarks>
internal sealed class CacheResetService(
    JbRunLock runLock,
    JbRunYield runYield,
    ILogger<CacheResetService> logger,
    TimeSpan? heartbeatInterval = null)
{
    /// <summary>
    ///     How many times to try one generation before reporting it; <see cref="DeleteRetryDelay" /> is the
    ///     gap between attempts.
    /// </summary>
    /// <remarks>
    ///     <see cref="ProcessRunner" /> waits only a bounded time for a killed <c>jb</c> tree to be reaped and
    ///     then rethrows anyway, so cancelling a pre-warm can hand this call the lease while that tree is still
    ///     alive holding memory-mapped cache files. Those handles go within a moment or they do not go at all,
    ///     so a few hundred milliseconds turns the common case into a clean drop while an undeletable directory
    ///     still reaches the report in well under a second.
    /// </remarks>
    private const int DeleteAttempts = 3;

    /// <summary>What a progress message calls this work.</summary>
    /// <remarks>
    ///     Not a <c>jb</c> subcommand, because there is no process here to name: a caller told it was watching
    ///     <c>inspectcode</c> would be looking for a run that does not exist.
    /// </remarks>
    private const string ProgressLabel = "cache reset";

    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    ///     Deletes the solution's own cache generations under the run lock, and reports what it dropped, left
    ///     alone and could not delete.
    /// </summary>
    /// <remarks>
    ///     <paramref name="onProgress" /> receives the queue wait as rendered lines while it is served out:
    ///     plain strings, so no MCP type reaches this layer.
    /// </remarks>
    public async Task<CacheResetOutcome> RunAsync(
        ResolvedConfig config,
        CancellationToken cancellationToken,
        Action<string>? onProgress = null)
    {
        // First statement, with no await before it, so the claim is raised by the time this method hands its
        // task back, and a pre-warm starting a moment later reads the count rather than racing it.
        // Disposal is the reverse of declaration, so the claim outlives the lease by a hair: a pre-warm
        // arriving in that window finds a free generation but a raised count and stands down, which is the
        // safe direction to be wrong in.
        using IDisposable foreground = runYield.EnterForeground();
        using IDisposable runLease = await AcquireReportingAsync(config, onProgress, cancellationToken);

        // Enumerated inside the lock, so the set found is the set deleted: outside it, a run starting in the
        // gap could fork a generation this call would then leave behind while reporting a clean reset. Which
        // of the same-named generations are this solution's own is FindFor's proof; this only decides what
        // happens to each half.
        JbSolutionGenerations generations = Find(config.CacheHome, config.SolutionPath);

        // Read inside the lock with the enumeration it describes, so no run can stamp a marker between the
        // two and leave the report attributing a generation to a path it no longer names — and only when
        // there is a neighbour to attribute, since the sweep reads every marker under a cache home shared by
        // every solution on the machine.
        Dictionary<string, string?> recordedPaths = generations.Neighbours.Count > 0
            ? JbWarmMarker.FindRecordedSolutionPaths(config.CacheHome, logger)
            : [];
        List<LeftAloneGeneration> leftAlone = generations.Neighbours
            .Select(generation => Attribute(generation, recordedPaths))
            .ToList();

        // One stat, taken here rather than carried in on the config: this is the only decision that turns on
        // it, and asking the filesystem directly keeps the config free of a field only this call reads.
        bool solutionFileExists = File.Exists(config.SolutionPath);

        List<string> dropped = [];
        List<CacheResetFailure> failures = [];
        foreach (JbCacheGeneration generation in generations.Owned)
        {
            CacheResetFailure? failure = await TryDeleteAsync(generation, cancellationToken);

            if (failure is null)
                dropped.Add(generation.Name);
            else
                failures.Add(failure);
        }

        // The marker claims a jb run against this generation succeeded recently, which stops the next session
        // pre-warming and offers the generation as a donor. After a reset that claim is false however the
        // deletes went, and a redundant pre-warm is the marker's one safe failure direction.
        JbWarmMarker.Clear(config.SolutionPath, config.CacheHome, logger);

        JbCostRecord.Clear(config.SolutionPath, config.CacheHome, logger);

        // Unconditional for a checkout that is still there, including the reset that dropped nothing: what
        // the caller asked for is a cold next run, and the transplanter, the one mechanism that could supply a
        // warm cache instead, has to be told. A reclaim is the other case: there is no next run against a path
        // with no checkout on it, and a tombstone left there would outlive the deletion and deny a worktree
        // re-created at that path the seeding any other new checkout gets, so it clears one instead.
        if (solutionFileExists)
            JbColdTombstone.Write(config.SolutionPath, config.CacheHome, logger);
        else
            JbColdTombstone.Clear(config.SolutionPath, config.CacheHome, logger);

        // The one tool that spawns no jb, so without this line it leaves no trace: a reset is the reason the
        // next call is slow, and read from the log afterwards that call would look cold for no reason.
        logger.LogInformation(
            "Reset the ReSharper cache for solution {SolutionPath} (solution file present: {SolutionFileExists}): "
            + "dropped {DroppedCount} generation(s) {Dropped}, "
            + "left {LeftAloneCount} belonging to another copy of this solution alone, {FailureCount} could not be deleted; "
            + "{Consequence}",
            config.SolutionPath,
            solutionFileExists,
            dropped.Count,
            dropped,
            leftAlone.Count,
            failures.Count,
            solutionFileExists
                ? "the next run against it is cold on purpose"
                : "the solution file does not exist, so this reclaimed the cache of a removed checkout");

        return new CacheResetOutcome(config.SolutionPath, config.CacheHome, dropped, leftAlone, failures, solutionFileExists);
    }

    /// <summary>
    ///     Attributes <paramref name="generation" /> to a checkout, as far as the markers under the cache home
    ///     can say.
    /// </summary>
    /// <remarks>
    ///     Absent from <paramref name="recordedPaths" /> and present with a null are different answers: the first
    ///     is a generation no successful run ever stamped, the second a marker from a build that recorded no
    ///     path.
    /// </remarks>
    private static LeftAloneGeneration Attribute(
        JbCacheGeneration generation,
        Dictionary<string, string?> recordedPaths)
    {
        if (!recordedPaths.TryGetValue(generation.Name, out string? solutionPath))
            return new LeftAloneGeneration(generation.Name, LeftAloneAttribution.NoRunOnRecord);

        if (solutionPath is null) return new LeftAloneGeneration(generation.Name, LeftAloneAttribution.PathNotRecorded);

        LeftAloneAttribution attribution = File.Exists(solutionPath)
            ? LeftAloneAttribution.CheckoutPresent
            : LeftAloneAttribution.CheckoutGone;

        return new LeftAloneGeneration(generation.Name, attribution, solutionPath);
    }

    /// <summary>
    ///     Queues for the generation's lease, saying so while the wait lasts.
    /// </summary>
    /// <remarks>
    ///     A reset spawns nothing, so the queue wait, up to the lock's cap, is the only silence it has to break.
    ///     The reporter is scoped to the acquisition alone, which is why the wait has a method of its own rather
    ///     than a wider <c>await using</c> in <see cref="RunAsync" />: no beat can land during the deletes and
    ///     describe a wait that has already ended, and on an acquire that gives up the reporter is disposed as
    ///     the <see cref="UserErrorException" /> unwinds past it, so nothing reports against a call already
    ///     answered with an error.
    /// </remarks>
    private async Task<IDisposable> AcquireReportingAsync(
        ResolvedConfig config,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        // The lock's own cap rather than a second copy of it, since that is the number bounding this wait. It
        // opens on the lock's queue: a reset takes no slot.
        await using JbRunProgress? progress = JbRunProgress.Reporting(
            ProgressLabel, config.SolutionPath, runLock.MaxWait, onProgress, logger, JbRunPhase.Queued, heartbeatInterval);

        return await runLock.AcquireAsync(config.SolutionPath, config.CacheHome, cancellationToken);
    }

    /// <summary>
    ///     Deletes one generation, giving a process that is on its way out a moment to let go, and returns
    ///     what stopped it rather than throwing.
    /// </summary>
    /// <remarks>
    ///     A recursive delete is idempotent after a partial failure (whatever came off stays off), so a retry
    ///     resumes rather than starting over.
    /// </remarks>
    private static async Task<CacheResetFailure?> TryDeleteAsync(
        JbCacheGeneration generation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1;; attempt++)
        {
            try
            {
                Directory.Delete(generation.FullPath, true);
                return null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == DeleteAttempts) return new CacheResetFailure(generation.Name, exception.Message);
            }

            await Task.Delay(DeleteRetryDelay, cancellationToken);
        }
    }

    /// <summary>
    ///     Finds the solution's generations with <see cref="JbCacheGenerations.FindFor" />, turning its
    ///     enumeration failures into a reportable error.
    /// </summary>
    /// <remarks>
    ///     A cache home this server cannot read is not "nothing to drop": answering a delete request with a
    ///     clean report would misstate what happened.
    /// </remarks>
    private static JbSolutionGenerations Find(string cacheHome, string solutionPath)
    {
        try
        {
            return JbCacheGenerations.FindFor(cacheHome, solutionPath);
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            throw new UserErrorException(
                $"Could not read the ReSharper cache home \"{cacheHome}\" ({ConfigWarningBanner.SingleLine(exception.Message)}).", exception);
        }
    }
}