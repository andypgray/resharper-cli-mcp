using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Formatting;

namespace Zphil.ReSharperCli.Services;

/// <summary>A generation that could not be deleted, and what the filesystem said about it.</summary>
internal sealed record CacheResetFailure(string Name, string Reason);

/// <summary>
///     What a reset did: the generation directory names it dropped, the ones belonging to a different
///     solution it deliberately left where they were, and the ones it could not delete, under the cache home
///     it looked in. Formatting lives in <c>CacheResetFormatter</c>.
/// </summary>
internal sealed record CacheResetOutcome(
    string SolutionPath,
    string CacheHome,
    IReadOnlyList<string> Dropped,
    IReadOnlyList<string> LeftAlone,
    IReadOnlyList<CacheResetFailure> Failures);

/// <summary>
///     Deletes the solution's ReSharper cache generations so the next <c>jb</c> run rebuilds its analysis
///     from cold — the invalidation the ReSharper CLI itself does not expose. <c>--caches-home</c> chooses
///     where caches live and nothing documents clearing or rebuilding one, so a stale index has no cure
///     short of deleting the directory; this server picks that directory, so it is the only thing placed to
///     offer the operation safely.
/// </summary>
/// <remarks>
///     <para>
///         "Safely" is the whole reason this is server-side rather than advice to delete a glob. The
///         <see cref="JbRunLock" /> for the (solution, cache home) pair is held across the delete, so a reset
///         queues behind a run in flight instead of pulling the cache out from under it — and a run that
///         starts mid-delete queues behind the reset. Told to delete a glob itself, an agent would race
///         another session's live <c>jb</c> with nothing to serialize on.
///     </para>
///     <para>
///         It waits for a run, but not for speculative work: <see cref="JbRunYield" /> is entered before the
///         lock, so a pre-warm holding the generation is stood down rather than waited out. Queueing behind
///         one would be the worst possible trade — the user is waiting on a call whose whole purpose is to
///         delete what that pass is busy building — and taking the lock without the claim is what left this
///         tool doing exactly that for up to the full run cap.
///     </para>
///     <para>
///         What belongs to this solution is settled by <see cref="JbSolutionCacheHash" /> rather than by the
///         generation's file name, which several checkouts of one repository share. A generation is deleted
///         only where the hash in its name is the one this solution's path produces; everything else is
///         reported as left alone, and a computed hash matching nothing deletes nothing at all. That is what
///         makes a shared cache home ordinary rather than an obstacle — before, two checkouts in one cache
///         home made the tool refuse for both.
///     </para>
///     <para>
///         A failed delete is retried briefly and then reported rather than thrown, and may leave a
///         generation partly deleted. That is deliberate: a directory jb rebuilds is a better outcome than a
///         call that fails after deleting most of one, and this tool is idempotent, so re-running finishes
///         the job. All the report can say about <em>why</em> is what the filesystem said, which is the
///         honest limit — the holder is usually a <c>jb</c> in another session, but since this call now
///         cancels a speculative run rather than waiting it out, it can equally be one this server killed a
///         moment ago and has not finished reaping.
///     </para>
/// </remarks>
/// <param name="heartbeatInterval">
///     How often a queued reset reports itself, defaulting to
///     <see cref="JbRunProgress.HeartbeatInterval" />. A parameter for the reason <see cref="JbRunner" />'s
///     own is: a test waiting out more than one beat should not have to pay ten seconds for it.
/// </param>
internal sealed class CacheResetService(
    JbRunLock runLock,
    JbRunYield runYield,
    ILogger<CacheResetService> logger,
    TimeSpan? heartbeatInterval = null)
{
    /// <summary>
    ///     How hard to try one generation before reporting it, and how long to leave between attempts.
    ///     <see cref="ProcessRunner" /> waits up to five seconds for a killed <c>jb</c> tree to be reaped
    ///     and then rethrows anyway, so cancelling a pre-warm can hand this call the lease while that tree
    ///     is still alive holding memory-mapped cache files. Those handles go within a moment or they do not
    ///     go at all, so a few hundred milliseconds turns the common case into a clean drop while a
    ///     genuinely undeletable directory still reaches the report about as fast as before.
    /// </summary>
    private const int DeleteAttempts = 3;

    /// <summary>
    ///     What a progress message calls this work. Not a <c>jb</c> subcommand, because there is no process
    ///     here to name: a reset takes the lock and deletes directories, and a caller told it was watching
    ///     <c>inspectcode</c> would be looking for a run that does not exist.
    /// </summary>
    private const string ProgressLabel = "cache reset";

    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(200);

    /// <remarks>
    ///     <paramref name="onProgress" /> is where the queue wait is reported while it is still being served
    ///     out — trailing and optional as <see cref="JbRunner.RunAsync" />'s is, and rendered rather than
    ///     structured for the same reason: the MCP types a notification is built from stop at the tool
    ///     surface.
    /// </remarks>
    public async Task<CacheResetOutcome> RunAsync(
        ResolvedConfig config,
        CancellationToken cancellationToken,
        Action<string>? onProgress = null)
    {
        // First statement, with no await before it, so the claim is provably raised by the time this method
        // hands its task back — a pre-warm starting a moment later reads the count rather than racing it.
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
        List<string> leftAlone = generations.Neighbours.Select(generation => generation.Name).ToList();

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

        // The marker claims a jb run against this generation succeeded recently, which is what stops the next
        // session pre-warming. After a reset that claim is false however the deletes went, and the marker's
        // one safe failure direction is a redundant pre-warm. Clearing it also withdraws this solution as a
        // donor for anything else, which is the same statement pointed outwards.
        JbWarmMarker.Clear(config.SolutionPath, config.CacheHome, logger);

        // Unconditional, including the reset that dropped nothing: what the caller asked for is a cold next
        // run, and the one mechanism that could quietly supply a warm cache instead has to be told.
        JbColdTombstone.Write(config.SolutionPath, config.CacheHome, logger);

        // The one tool that spawns no jb, and until now the one that left no trace: a reset is the reason the
        // next call is slow, and read from the log afterwards that call looked cold for no reason.
        logger.LogInformation(
            "Reset the ReSharper cache for solution {SolutionPath}: dropped {DroppedCount} generation(s) {Dropped}, "
            + "left {LeftAloneCount} belonging to another copy of this solution alone, {FailureCount} could not be deleted; "
            + "the next run against it is cold on purpose",
            config.SolutionPath,
            dropped.Count,
            dropped,
            leftAlone.Count,
            failures.Count);

        return new CacheResetOutcome(config.SolutionPath, config.CacheHome, dropped, leftAlone, failures);
    }

    /// <summary>
    ///     Queue for the generation's lease, saying so while the wait lasts. This call spawns nothing, so
    ///     the wait is the whole of the silence it has to break — and up to the lock's own cap of it.
    /// </summary>
    /// <remarks>
    ///     The reporter is scoped to the acquisition and to nothing else, which is why the wait has a method
    ///     of its own rather than a wider <c>await using</c> in <see cref="RunAsync" />. Two things fall out
    ///     of it. A beat cannot land during the deletes, which take moments and would be described as a wait
    ///     that had already ended. And on a contended acquire the reporter is disposed as the
    ///     <see cref="UserErrorException" /> unwinds past it, so nothing reports against a call that has
    ///     already been answered with an error.
    /// </remarks>
    private async Task<IDisposable> AcquireReportingAsync(
        ResolvedConfig config,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        // The lock's own cap rather than a second copy of it: this caller is bounded by that number, so it
        // is the only honest one to name. It never reaches a message as things stand — the cap is armed by
        // Spawning, and nothing here spawns — but a lifecycle that grew a later phase would name the number
        // that actually bounds it.
        await using JbRunProgress? progress = JbRunProgress.Reporting(
            ProgressLabel, config.SolutionPath, runLock.MaxWait, onProgress, logger, heartbeatInterval);

        return await runLock.AcquireAsync(config.SolutionPath, config.CacheHome, cancellationToken);
    }

    /// <summary>
    ///     Delete one generation, giving a process that is on its way out a moment to let go, and returning
    ///     what stopped it rather than throwing. A recursive delete is idempotent after a partial failure —
    ///     whatever came off stays off — so a retry resumes rather than starting over.
    /// </summary>
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
    ///     <see cref="JbCacheGenerations.FindFor" /> with its enumeration failures turned into a reportable
    ///     error. A cache home this server cannot read is not "nothing to drop" — answering a delete request
    ///     with a clean report would be the worst possible reading of it.
    /// </summary>
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