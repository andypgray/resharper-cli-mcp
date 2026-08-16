using Serilog;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Services;

/// <summary>
///     Gives a solution with no ReSharper cache a running start by copying one belonging to another copy of
///     the same solution — a second worktree, a clone, a build directory — under the name <c>jb</c> will look
///     for.
/// </summary>
/// <remarks>
///     <para>
///         <c>jb</c> keys a cache generation by the solution's absolute path, so every fresh worktree of a
///         repository already analysed is cold, however much analysis of the same code sits beside it. Cold is
///         not merely slow on a large solution: it is the case that runs past the cap and comes back as a
///         timeout, so the pre-warm built for it never gets the chance to help a session that works in a
///         worktree.
///     </para>
///     <para>
///         Nothing inside the cache binds it to a path — <c>jb</c> validates a generation against its own
///         format version and rebuilds it in place when that does not match — so a copy under another hash is
///         either accepted and re-keyed by the run that opens it, or wiped and rebuilt exactly as an absent
///         one would be. That is what makes this safe to build on a naming scheme nobody documents: a copy
///         <c>jb</c> refuses costs the copy and nothing else.
///     </para>
///     <para>
///         It is a trade rather than a free win, and the arithmetic decides where it belongs. A seeded run
///         pays to re-key the copy and to analyse whatever the donor's checkout never saw, and that premium
///         is not fixed: measured at roughly a minute over the warm run that followed it on one repository's
///         worktrees, and at about six minutes on a larger donor whose checkout had drifted further. What it
///         buys is however much a warm cache is worth on that solution. On one whose cold analysis runs past
///         the cap, that is the difference between a result and a timeout — 456 s seeded and returning,
///         against the same call capping out before. On one that goes cold in a minute or two, the premium
///         can cost more than the rebuild it replaced. The copy itself is not what makes it a trade: the
///         largest generation in a censused cache home, 277 MB across 188 files, copied in under two seconds.
///     </para>
///     <para>
///         No size threshold guards it, and that census is the argument that none can be placed from disk. A
///         generation's size tracks how much analysis has accumulated against that path, not how heavy the
///         solution is — most of the fresh checkouts of the largest solution there measured smaller than an
///         aged cache of a small one — so nothing available before the run separates "rescues a call that
///         would have timed out" from "adds a minute to one that would have been fine". The losing case is
///         bounded and one-time, a single re-key per new checkout; the winning case is a call that returns
///         at all.
///     </para>
///     <para>
///         Every step may decline. No donor, an unreadable marker, a busy donor, a copy that fails halfway,
///         and a solution whose cache was just reset all end the same way — no seed, no error, and the cold
///         run the call was going to have anyway. The one thing it must never do is act on a maybe. So the
///         donor has to be named by a marker a successful run wrote, and it acts on the target only where no
///         run against that path has ever succeeded: no generation at all, or generations with no warm marker
///         beside them, since every successful run stamps one and any marker — naming a generation or empty —
///         protects what is on disk. Even then nothing is deleted until the full copy is standing beside the
///         slot, so a failure costs the copy rather than the cache. A reset ends the whole thing outright,
///         because <c>resharper_reset_cache</c> composes with this and guessing does not.
///     </para>
/// </remarks>
/// <param name="runLock">
///     The same lock every other cache-home operation takes. The caller already holds the <em>target</em>
///     generation's lease; this takes the <em>donor</em>'s for the length of the copy, so a solution being
///     analysed elsewhere is not read from mid-write.
/// </param>
/// <param name="donorLockPatience">
///     How long to wait for the donor's lease before giving up, defaulting to
///     <see cref="DefaultDonorLockPatience" />. Small on purpose: it is spent while holding the target's
///     lease, and a donor that is busy is a donor to skip rather than to queue for.
/// </param>
internal sealed class CacheTransplanter(JbRunLock runLock, TimeSpan? donorLockPatience = null)
{
    /// <summary>
    ///     Marks a directory as a copy still being made. The trailing token is not digits, so
    ///     <see cref="JbCacheGenerations" /> does not read the directory as a generation while it is
    ///     incomplete — and neither does a reset, which would otherwise be able to delete it mid-copy.
    ///     Internal so the parser's tests can pin that invisibility against the real suffix.
    /// </summary>
    internal const string InProgressSuffix = ".transplanting";

    /// <summary>
    ///     Long enough to outlast a donor's marker being rewritten at the end of someone else's run, long
    ///     enough to outlast the reap of a pre-warm this very caller has just cancelled, and short enough to
    ///     stay invisible against the cold analysis it exists to avoid.
    /// </summary>
    /// <remarks>
    ///     The second of those is what fixes the number rather than merely bounding it, and it is the one
    ///     nothing here can see: a caller the user is waiting on cancels the speculative pass before it
    ///     queues for its own lease (<see cref="JbRunner" />), and across two solutions that lease is
    ///     uncontended and granted at once — so it can arrive here while the pass it just killed is still
    ///     holding the donor's. That lease drops only once <see cref="ProcessRunner" /> has reaped the
    ///     killed tree, so waiting less than <see cref="ProcessRunner.KilledTreeReapBudget" /> would turn a
    ///     cancelled pre-warm into a declined donor and a cold run — the exact run this exists to avoid, in
    ///     the worktree configuration it was built for. Equality is enough without a margin because the kill
    ///     begins strictly before this wait does.
    /// </remarks>
    internal static readonly TimeSpan DefaultDonorLockPatience = ProcessRunner.KilledTreeReapBudget;

    private readonly TimeSpan _donorLockPatience = donorLockPatience ?? DefaultDonorLockPatience;

    /// <summary>
    ///     Seed the cache for <paramref name="config" />'s solution from a sibling's, reporting whether one
    ///     was actually planted. The caller must already hold the target generation's run lease, and must be
    ///     about to run <c>jb</c> against it: this leaves an unvalidated copy behind, and only <c>jb</c>
    ///     opening it settles whether it was any use.
    /// </summary>
    /// <remarks>
    ///     Cancellation is the one thing that propagates. Everything else is swallowed, because this runs on
    ///     the way into a call the user made and has no claim on failing it.
    /// </remarks>
    public async Task<bool> TryTransplantAsync(ResolvedConfig config, CancellationToken cancellationToken)
    {
        try
        {
            return await SeedAsync(config, cancellationToken);
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            Log.Debug(exception, "Could not look for a ReSharper cache to seed solution {SolutionPath} from", config.SolutionPath);
            return false;
        }
    }

    private async Task<bool> SeedAsync(ResolvedConfig config, CancellationToken cancellationToken)
    {
        // Owned generations only: the donor shares the solution file name by definition, so a check for
        // "this solution has no generations at all" would find the donor's and never fire.
        //
        // What makes generations that are already there replaceable is the marker's absence, and nothing
        // else. Every successful run stamps one, so a marker — any marker, the empty legacy form included —
        // says a run produced what is on disk and this has no business touching it. No marker at all says no
        // run against this path ever finished, leaving the part-built remnant of one that was killed: worth
        // less than the copy, and the reason a first run that dies at the cap would otherwise never be
        // seeded again.
        JbSolutionGenerations generations = JbCacheGenerations.FindFor(config.CacheHome, config.SolutionPath);
        if (generations.Owned.Count > 0 && JbWarmMarker.Exists(config.SolutionPath, config.CacheHome)) return false;

        if (JbColdTombstone.Exists(config.SolutionPath, config.CacheHome)) return false;

        if (FindDonor(config) is not { } donor) return false;

        using IDisposable? donorLease = await runLock.TryAcquireByKeyAsync(
            config.CacheHome, donor.Key, _donorLockPatience, cancellationToken);

        if (donorLease is null) return false;

        string generationName = JbSolutionCacheHash.FirstGenerationDirectoryName(config.SolutionPath);
        return Copy(
            JbCacheGenerations.PathUnder(config.CacheHome, donor.GenerationName),
            JbCacheGenerations.PathUnder(config.CacheHome, generationName),
            config.SolutionPath,
            generations.Owned,
            cancellationToken);
    }

    /// <summary>
    ///     The most recently warmed cache generation under this cache home built from a solution file of the
    ///     same name at a <em>different</em> path, or <see langword="null" /> when there is none to copy.
    /// </summary>
    /// <remarks>
    ///     Donors are found through the warm markers rather than by reading directory names, because a
    ///     directory only says a cache exists while a marker says a <c>jb</c> run against it finished cleanly
    ///     — the difference between a cache worth copying and the abandoned husk of a run that was killed.
    ///     The first candidate is the only candidate: falling through to a second-best donor would spend the
    ///     caller's time on a chain of attempts to save a cold run that is already running late.
    /// </remarks>
    private static Donor? FindDonor(ResolvedConfig config)
    {
        string ourKey = JbSidecar.ComputeKey(config.SolutionPath, config.CacheHome);

        List<Donor> candidates = [];
        foreach ((string key, string markerPath) in JbWarmMarker.FindAll(config.CacheHome))
        {
            if (string.Equals(key, ourKey, StringComparison.Ordinal)) continue;

            if (JbWarmMarker.TryReadGenerationName(markerPath, config.CacheHome) is not { } generationName) continue;

            if (!JbCacheGenerations.IsNeighbourOf(generationName, config.SolutionPath)) continue;

            candidates.Add(new Donor(key, generationName, File.GetLastWriteTimeUtc(markerPath)));
        }

        return candidates.MaxBy(candidate => candidate.WarmedAt);
    }

    /// <summary>
    ///     Copy the donor's tree into place, replacing whichever <paramref name="replaced" /> generations are
    ///     there — the leftovers of runs that never finished, and usually none. Built somewhere <c>jb</c> and
    ///     this server's own reset both ignore, and moved into position at the end, so a copy that fails or is
    ///     cancelled adds no directory a later run could open as a cache: within one parent the move is a
    ///     rename, which cannot be observed half-done.
    /// </summary>
    /// <remarks>
    ///     The order is the safety property, and it is one-way: the copy is complete and standing beside the
    ///     slot before anything is deleted, so every way this can fail up to that point leaves what was on
    ///     disk exactly where it was, and the run about to start resumes it. Only the rename spends that
    ///     safety, and it cannot be observed half-done. What is accepted in exchange is a slot delete that
    ///     fails part way through, on the same terms as the reset's own: <c>jb</c> validates a generation
    ///     against its format version and rebuilds it in place, so the worst residue is a cold run.
    /// </remarks>
    private static bool Copy(
        string donorPath,
        string targetPath,
        string solutionPath,
        IReadOnlyList<JbCacheGeneration> replaced,
        CancellationToken cancellationToken)
    {
        string inProgressPath = targetPath + InProgressSuffix;

        try
        {
            // A copy this one abandoned, from a process that was killed mid-run. Deletable because the caller
            // holds the target's lease, so nothing else can be building it.
            if (Directory.Exists(inProgressPath)) Directory.Delete(inProgressPath, true);

            CopyTree(donorPath, inProgressPath, cancellationToken);

            if (!TryClearTargetSlot(targetPath, solutionPath))
            {
                Discard(inProgressPath);
                return false;
            }

            Directory.Move(inProgressPath, targetPath);
            SweepReplacedForks(targetPath, replaced);

            if (replaced.Count > 0)
            {
                Log.Information(
                    "Seeded the ReSharper cache for solution {SolutionPath} by copying {DonorGeneration} over {ReplacedGenerations}, the part-built remnant of a run that never finished; the run about to start re-keys it",
                    solutionPath,
                    Path.GetFileName(donorPath),
                    replaced.Select(generation => generation.Name).ToList());

                return true;
            }

            Log.Information(
                "Seeded the ReSharper cache for solution {SolutionPath} by copying {DonorGeneration} to {TargetGeneration}; the run about to start re-keys it",
                solutionPath,
                Path.GetFileName(donorPath),
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
            Log.Warning(
                exception,
                "Could not seed the ReSharper cache for solution {SolutionPath} from {DonorGeneration}; the run will build it from cold instead",
                solutionPath,
                Path.GetFileName(donorPath));

            return false;
        }
    }

    /// <summary>
    ///     Empty the generation slot the finished copy is about to be renamed into, reporting whether it is
    ///     now clear. Nothing there is the ordinary case and is clear for free; what this exists for is the
    ///     part-built remnant <see cref="SeedAsync" /> has just proved no successful run produced.
    /// </summary>
    /// <remarks>
    ///     A delete that fails costs the copy and not the cache: the remnant stays whole, the aside is
    ///     discarded, and the run about to start resumes exactly what it would have resumed anyway. A
    ///     <em>file</em> at the slot is not a directory to delete and reads as clear, so it goes on to fail at
    ///     the move, which is what it did before this method existed.
    /// </remarks>
    private static bool TryClearTargetSlot(string targetPath, string solutionPath)
    {
        if (!Directory.Exists(targetPath)) return true;

        try
        {
            Directory.Delete(targetPath, true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning(
                exception,
                "Could not clear the part-built ReSharper cache {TargetGeneration} for solution {SolutionPath}; the run about to start resumes it instead of the copy that was ready to replace it",
                Path.GetFileName(targetPath),
                solutionPath);

            return false;
        }
    }

    /// <summary>
    ///     Remove what is left of the replaced generations once the copy has landed: the forks a concurrent
    ///     <c>jb</c> made of the remnant, which the slot delete does not cover.
    /// </summary>
    /// <remarks>
    ///     Deliberately after the rename and deliberately best effort, which together shrink the delete the
    ///     seeding depends on to one directory. A fork that will not go costs disk and nothing else: <c>jb</c>
    ///     opens the first generation, which is the one just seeded, and the marker the next successful run
    ///     stamps names that one too — so a survivor is reclaimed by the next cache reset rather than being
    ///     worth failing over.
    /// </remarks>
    private static void SweepReplacedForks(string targetPath, IReadOnlyList<JbCacheGeneration> replaced)
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
                Log.Debug(
                    exception,
                    "Could not remove {ForkGeneration}, a fork of the part-built cache just replaced; a cache reset reclaims it",
                    generation.Name);
            }
        }
    }

    /// <summary>
    ///     Recursive copy by hand rather than a library helper, so cancellation is honoured between files
    ///     rather than only between whole trees — a cache generation is hundreds of megabytes, and the caller
    ///     cancels when the user's own run wants to start.
    /// </summary>
    /// <remarks>
    ///     Reparse points are stepped over rather than followed. Nothing <c>jb</c> writes contains one, so
    ///     skipping costs nothing real, and following one in a cache home somebody has been rearranging could
    ///     copy a directory tree into itself.
    /// </remarks>
    private static void CopyTree(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationPath);

        // One enumeration serves both kinds of entry, and Attributes comes from its find data — no
        // per-directory re-stat on the only path in this server that walks hundreds of megabytes.
        foreach (FileSystemInfo entry in new DirectoryInfo(sourcePath).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry is not DirectoryInfo)
            {
                File.Copy(entry.FullName, Path.Combine(destinationPath, entry.Name));
                continue;
            }

            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

            CopyTree(entry.FullName, Path.Combine(destinationPath, entry.Name), cancellationToken);
        }
    }

    /// <summary>
    ///     Remove a copy that will not be finished. Best effort: what is left behind if this fails is inert —
    ///     no parser reads it as a cache generation — and the next attempt deletes it before starting.
    /// </summary>
    private static void Discard(string inProgressPath)
    {
        try
        {
            if (Directory.Exists(inProgressPath)) Directory.Delete(inProgressPath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Debug(exception, "Could not remove the abandoned partial cache copy {InProgressPath}", inProgressPath);
        }
    }

    /// <summary>A cache generation worth copying, and what it takes to lock it.</summary>
    private sealed record Donor(string Key, string GenerationName, DateTime WarmedAt);
}