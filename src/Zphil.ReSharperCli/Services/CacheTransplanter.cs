using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Services;

/// <summary>
///     Gives a solution with no ReSharper cache a running start by copying one belonging to another copy of
///     the same solution (a second worktree, a clone, a build directory) under the name <c>jb</c> will look
///     for.
/// </summary>
/// <remarks>
///     <para>
///         <c>jb</c> keys a cache generation by the solution's absolute path, so every fresh worktree of a
///         repository already analysed is cold, however much analysis of the same code sits beside it, and on a
///         large solution cold is the case that runs past the cap.
///     </para>
///     <para>
///         Nothing inside the cache binds it to a path: <c>jb</c> validates a generation against its own format
///         version and rebuilds it in place when that does not match, so a copy under another hash is either
///         accepted and re-keyed by the run that opens it, or wiped and rebuilt as an absent one would be. That
///         is what makes this safe to build on a naming scheme nobody documents: a copy <c>jb</c> refuses costs
///         the copy and nothing else. No size threshold guards it, because a generation's size tracks how much
///         analysis has accumulated against its path rather than how heavy the solution is, so nothing on disk
///         predicts which side of the seeding trade a run lands on.
///     </para>
///     <para>
///         Every step may decline, and every decline ends the same way: no seed, no error, and the cold run the
///         call was going to have anyway. It never acts on a maybe.
///     </para>
///     <para>
///         <paramref name="runLock" /> is the lock every other cache-home operation takes. The caller already
///         holds the target generation's lease; this takes the donor's for the length of the copy, so a
///         solution being analysed elsewhere is not read from mid-write. <paramref name="donorLockPatience" />
///         bounds that wait, defaulting to <see cref="DefaultDonorLockPatience" />, and stays small because it is
///         spent while holding the target's lease: a busy donor is one to skip rather than to queue for.
///     </para>
/// </remarks>
internal sealed class CacheTransplanter(
    JbRunLock runLock,
    ILogger<CacheTransplanter> logger,
    // A seam: a parameter so a test of a busy donor need not wait out the production patience.
    TimeSpan? donorLockPatience = null)
{
    /// <summary>
    ///     Marks a directory as a copy still being made.
    /// </summary>
    /// <remarks>
    ///     The trailing token is not digits, so <see cref="JbCacheGenerations" /> does not read the directory
    ///     as a generation while it is incomplete — and neither does a reset, which would otherwise be able to
    ///     delete it mid-copy.
    /// </remarks>
    internal const string InProgressSuffix = ".transplanting";

    /// <summary>
    ///     Long enough to outlast a donor's marker being rewritten at the end of someone else's run, and
    ///     short enough to stay invisible against the cold analysis it exists to avoid.
    /// </summary>
    /// <remarks>
    ///     <see cref="ProcessRunner.KilledTreeReapBudget" /> is a measured bound on how long a departing holder
    ///     takes to let go, and a donor's lease can be held by one: a <c>jb</c> on its way out in another
    ///     server process, or one started by hand. Within this process <see cref="JbRunSlot" /> already orders
    ///     the case: a killed speculative pass holds this server's slot until <see cref="ProcessRunner" /> has
    ///     reaped it, so a caller for another solution arrives after it rather than beside it.
    /// </remarks>
    internal static readonly TimeSpan DefaultDonorLockPatience = ProcessRunner.KilledTreeReapBudget;

    private readonly TimeSpan _donorLockPatience = donorLockPatience ?? DefaultDonorLockPatience;

    /// <summary>
    ///     Seeds the cache for <paramref name="config" />'s solution from a sibling's, reporting whether one
    ///     was planted.
    /// </summary>
    /// <remarks>
    ///     The caller must already hold the target generation's run lease, and must be about to run <c>jb</c>
    ///     against it: this leaves an unvalidated copy behind, and only <c>jb</c> opening it settles whether it
    ///     was any use. Cancellation propagates; an ordinary filesystem failure is swallowed, because this runs
    ///     on the way into a call the user made and has no claim on failing it.
    /// </remarks>
    public async Task<bool> TryTransplantAsync(ResolvedConfig config, CancellationToken cancellationToken)
    {
        try
        {
            return await SeedAsync(config, cancellationToken);
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            logger.LogDebug(exception, "Could not look for a ReSharper cache to seed solution {SolutionPath} from", config.SolutionPath);
            return false;
        }
    }

    /// <summary>Logs that nothing was planted, and why.</summary>
    /// <remarks>
    ///     A silent decline would make "declined to seed" and "never looked" the same observation, and that
    ///     distinction is what diagnoses a checkout that should have been seeded and ran cold instead. Split by
    ///     level on what the decline leaves behind: a target that is still cold is about to cost minutes and the
    ///     reason explains them, while declining over a cache that is already warm is the ordinary case on every
    ///     call of every session.
    /// </remarks>
    private void Declined(string solutionPath, string reason, bool leavesTargetCold)
    {
        if (leavesTargetCold)
        {
            logger.LogInformation(
                "Did not seed the ReSharper cache for solution {SolutionPath} ({DeclineReason}); the run about to start builds it from cold",
                solutionPath,
                reason);

            return;
        }

        logger.LogDebug(
            "Did not seed the ReSharper cache for solution {SolutionPath} ({DeclineReason})", solutionPath, reason);
    }

    private async Task<bool> SeedAsync(ResolvedConfig config, CancellationToken cancellationToken)
    {
        // Owned generations only: the donor shares the solution file name by definition, so a check for
        // "this solution has no generations at all" would find the donor's and never fire.
        //
        // What makes generations that are already there replaceable is the marker's absence, and nothing
        // else. Every successful run stamps one, so any marker (the empty form included) says a run produced
        // what is on disk and this has no business touching it. No marker at all says no run against this path
        // ever finished, leaving the part-built remnant of one that was killed: worth less than the copy, and
        // were it protected, a first run that dies at the cap would never be seeded again.
        JbSolutionGenerations generations = JbCacheGenerations.FindFor(config.CacheHome, config.SolutionPath);
        if (generations.Owned.Count > 0 && JbWarmMarker.Exists(config.SolutionPath, config.CacheHome, logger))
        {
            Declined(config.SolutionPath, "a run against it already succeeded", false);
            return false;
        }

        if (JbColdTombstone.Exists(config.SolutionPath, config.CacheHome, logger))
        {
            Declined(config.SolutionPath, "its cache was reset on purpose", true);
            return false;
        }

        if (FindDonor(config) is not { } donor)
        {
            Declined(config.SolutionPath, "no warm cache of another copy of this solution to copy", true);
            return false;
        }

        using IDisposable? donorLease = await runLock.TryAcquireByKeyAsync(
            config.CacheHome, donor.Key, _donorLockPatience, cancellationToken);

        if (donorLease is null)
        {
            Declined(config.SolutionPath, $"the donor {donor.GenerationName} is in use elsewhere", true);
            return false;
        }

        string generationName = JbSolutionCacheHash.FirstGenerationDirectoryName(config.SolutionPath);
        return Copy(
            JbCacheGenerations.PathUnder(config.CacheHome, donor.GenerationName),
            JbCacheGenerations.PathUnder(config.CacheHome, generationName),
            config.SolutionPath,
            generations.Owned,
            cancellationToken);
    }

    /// <summary>
    ///     Finds the most recently warmed cache generation under this cache home built from a solution file of
    ///     the same name at a different path.
    /// </summary>
    /// <remarks>
    ///     Donors are found through the warm markers rather than by reading directory names, because a
    ///     directory only says a cache exists while a marker says a <c>jb</c> run against it finished cleanly:
    ///     the difference between a cache worth copying and the abandoned husk of a run that was killed. The
    ///     first candidate is the only one tried, since falling through to a second-best donor would spend the
    ///     caller's time on a chain of attempts to save a cold run that is already running late.
    /// </remarks>
    private Donor? FindDonor(ResolvedConfig config)
    {
        string ourKey = JbSidecar.ComputeKey(config.SolutionPath, config.CacheHome);

        List<Donor> candidates = [];
        foreach ((string key, string markerPath) in JbWarmMarker.FindAll(config.CacheHome))
        {
            if (string.Equals(key, ourKey, StringComparison.Ordinal)) continue;

            WarmMarkerContent donor = JbWarmMarker.TryReadMarker(markerPath, config.CacheHome, logger);
            if (donor.GenerationName is not { } generationName) continue;

            if (!JbCacheGenerations.IsNeighbourOf(generationName, config.SolutionPath)) continue;

            // A donor another jb build wrote is worse than none: jb rebuilds a generation it did not write
            // in place, so the copy would buy a cold-shaped run and charge the seeding premium on top of it.
            // A check rather than a guarantee: a marker names the build its run was labelled with, which can
            // lag an in-place update (JbLocator's remarks name the window), and a mislabel only declines a donor
            // this could have used.
            if (JbWarmMarker.WrittenByAnotherBuild(donor.JbVersion, config.JbVersion)) continue;

            candidates.Add(new Donor(key, generationName, File.GetLastWriteTimeUtc(markerPath)));
        }

        return candidates.MaxBy(candidate => candidate.WarmedAt);
    }

    /// <summary>
    ///     Copies the donor's tree into place, replacing whichever <paramref name="replaced" /> generations are
    ///     there: the leftovers of runs that never finished, and usually none.
    /// </summary>
    /// <remarks>
    ///     Built under a name <c>jb</c> and this server's own reset both ignore, and moved into position at the
    ///     end: within one parent the move is a rename, which cannot be observed half-done, so a copy that fails
    ///     or is cancelled adds no directory a later run could open as a cache. Nothing is deleted until the copy
    ///     is complete beside the slot, so every failure before that point leaves what was on disk where it was,
    ///     for the run about to start to resume. What is accepted in exchange is a slot delete that fails part
    ///     way through, on the same terms as the reset's own: <c>jb</c> validates a generation against its format
    ///     version and rebuilds it in place, so the worst residue is a cold run.
    /// </remarks>
    private bool Copy(
        string donorPath,
        string targetPath,
        string solutionPath,
        IReadOnlyList<JbCacheGeneration> replaced,
        CancellationToken cancellationToken)
    {
        string inProgressPath = targetPath + InProgressSuffix;

        try
        {
            // A copy an earlier attempt abandoned, from a process that was killed mid-run. Deletable because the
            // caller holds the target's lease, so nothing else can be building it.
            if (Directory.Exists(inProgressPath)) Directory.Delete(inProgressPath, true);

            // Measured while copying rather than by a second walk, and logged because the seeded-run premium
            // scales with it: re-keying what was copied, not the copy itself, is what a seeded run spends its
            // extra time on.
            var copied = Stopwatch.StartNew();
            (long bytes, int files) = CopyTree(donorPath, inProgressPath, cancellationToken);
            copied.Stop();

            if (!TryClearTargetSlot(targetPath, solutionPath))
            {
                Discard(inProgressPath);
                return false;
            }

            Directory.Move(inProgressPath, targetPath);
            SweepReplacedForks(targetPath, replaced);

            if (replaced.Count > 0)
            {
                logger.LogInformation(
                    "Seeded the ReSharper cache for solution {SolutionPath} by copying {DonorGeneration} ({DonorBytes} bytes across {DonorFiles} files in {CopiedMs} ms) over {ReplacedGenerations}, the part-built remnant of a run that never finished; the run about to start re-keys it",
                    solutionPath,
                    Path.GetFileName(donorPath),
                    bytes,
                    files,
                    copied.ElapsedMilliseconds,
                    replaced.Select(generation => generation.Name).ToList());

                return true;
            }

            logger.LogInformation(
                "Seeded the ReSharper cache for solution {SolutionPath} by copying {DonorGeneration} ({DonorBytes} bytes across {DonorFiles} files in {CopiedMs} ms) to {TargetGeneration}; the run about to start re-keys it",
                solutionPath,
                Path.GetFileName(donorPath),
                bytes,
                files,
                copied.ElapsedMilliseconds,
                Path.GetFileName(targetPath));

            return true;
        }
        catch (OperationCanceledException)
        {
            Discard(inProgressPath);
            throw;
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            Discard(inProgressPath);
            logger.LogWarning(
                exception,
                "Could not seed the ReSharper cache for solution {SolutionPath} from {DonorGeneration}; the run will build it from cold instead",
                solutionPath,
                Path.GetFileName(donorPath));

            return false;
        }
    }

    /// <summary>
    ///     Empties the generation slot the finished copy is about to be renamed into, and reports whether it
    ///     is now clear.
    /// </summary>
    /// <remarks>
    ///     Nothing there is the ordinary case and is clear for free; what this exists for is the part-built
    ///     remnant <see cref="SeedAsync" /> has just proved no successful run produced. A recursive delete removes
    ///     what it can before it throws, so one that fails discards the copy and leaves what survived of the
    ///     remnant to the run about to start, at worst a cold run. A file at the slot is not a directory to
    ///     delete and reads as clear, so it goes on to fail at the move.
    /// </remarks>
    private bool TryClearTargetSlot(string targetPath, string solutionPath)
    {
        if (!Directory.Exists(targetPath)) return true;

        try
        {
            Directory.Delete(targetPath, true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "Could not clear the part-built ReSharper cache {TargetGeneration} for solution {SolutionPath}; the run about to start resumes it instead of the copy that was ready to replace it",
                Path.GetFileName(targetPath),
                solutionPath);

            return false;
        }
    }

    /// <summary>
    ///     Removes what is left of the replaced generations once the copy has landed: the forks a concurrent
    ///     <c>jb</c> made of the remnant, which the slot delete does not cover.
    /// </summary>
    /// <remarks>
    ///     It runs after the rename and is best effort, which together shrink the delete the seeding depends on
    ///     to one directory. A fork that will not go costs disk and nothing else: <c>jb</c> opens the first generation,
    ///     which is the one just seeded, and the marker the next successful run stamps names that one too, so a
    ///     survivor is reclaimed by the next cache reset rather than being worth failing over.
    /// </remarks>
    private void SweepReplacedForks(string targetPath, IReadOnlyList<JbCacheGeneration> replaced)
    {
        string slotName = Path.GetFileName(targetPath);

        foreach (JbCacheGeneration generation in replaced)
        {
            // By name, because the slot's own entry now addresses the copy that was just moved into it.
            if (string.Equals(generation.Name, slotName, JbCacheGenerations.NameComparison)) continue;

            try
            {
                Directory.Delete(generation.FullPath, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(
                    exception,
                    "Could not remove {ForkGeneration}, a fork of the part-built cache just replaced; a cache reset reclaims it",
                    generation.Name);
            }
        }
    }

    /// <summary>
    ///     Copies the tree at <paramref name="sourcePath" /> to <paramref name="destinationPath" />, honouring
    ///     cancellation between files.
    /// </summary>
    /// <remarks>
    ///     By hand rather than a library helper, so cancellation lands between files rather than only between
    ///     whole trees: a cache generation is hundreds of megabytes, and the caller cancels when the user's own
    ///     run wants to start. Reparse points are stepped over rather than followed. Nothing <c>jb</c> writes
    ///     contains one, so skipping costs nothing, and following one in a cache home somebody has been
    ///     rearranging could copy a directory tree into itself.
    /// </remarks>
    /// <returns>The bytes and file count copied, tallied from the same enumeration rather than by a second walk.</returns>
    private static (long Bytes, int Files) CopyTree(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationPath);

        long bytes = 0;
        var files = 0;

        // One enumeration serves both kinds of entry, and Attributes comes from its find data, so a walk of
        // hundreds of megabytes makes no per-directory re-stat.
        foreach (FileSystemInfo entry in new DirectoryInfo(sourcePath).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry is DirectoryInfo directory)
            {
                if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                (long nestedBytes, int nestedFiles) =
                    CopyTree(directory.FullName, Path.Combine(destinationPath, directory.Name), cancellationToken);

                bytes += nestedBytes;
                files += nestedFiles;
                continue;
            }

            File.Copy(entry.FullName, Path.Combine(destinationPath, entry.Name));

            // Off the find data this enumeration already carries, so the tally costs no extra stat.
            bytes += (entry as FileInfo)?.Length ?? 0;
            files++;
        }

        return (bytes, files);
    }

    /// <summary>Removes a copy that will not be finished.</summary>
    /// <remarks>
    ///     Best effort: what is left behind if this fails is inert, since no parser reads it as a cache
    ///     generation, and the next attempt deletes it before starting.
    /// </remarks>
    private void Discard(string inProgressPath)
    {
        try
        {
            if (Directory.Exists(inProgressPath)) Directory.Delete(inProgressPath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "Could not remove the abandoned partial cache copy {InProgressPath}", inProgressPath);
        }
    }

    /// <summary>Represents a cache generation worth copying, and what it takes to lock it.</summary>
    private sealed record Donor(string Key, string GenerationName, DateTime WarmedAt);
}