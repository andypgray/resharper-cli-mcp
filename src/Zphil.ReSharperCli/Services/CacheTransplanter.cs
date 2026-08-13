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
///         It is a trade rather than a free win, and the arithmetic decides where it belongs. Re-keying a
///         copied cache costs <c>jb</c> around a minute — measured at roughly the same figure on a small
///         solution and a large one, so treat it as close to fixed — against a saving of however much a warm
///         cache is worth on that solution. On one whose cold analysis runs past the cap, that is the
///         difference between a result and a timeout. On one that goes cold in a minute or two, the re-key
///         can cost more than the rebuild it replaced. No size threshold guards it today: two measurements
///         are not enough to place one, and the case it exists for is the case where the margin is enormous.
///     </para>
///     <para>
///         Every step may decline. No donor, an unreadable marker, a busy donor, a copy that fails halfway,
///         and a solution whose cache was just reset all end the same way — no seed, no error, and the cold
///         run the call was going to have anyway. The one thing it must never do is act on a maybe, so the
///         donor has to be named by a marker a successful run wrote, and the target generation has to be
///         genuinely absent: an existing one is left alone even when it is a stunted remnant of a run that was
///         killed, because <c>resharper_reset_cache</c> composes with this and guessing does not.
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
    ///     Long enough to outlast a donor's marker being rewritten at the end of someone else's run, short
    ///     enough to be invisible against the cold analysis it is trying to avoid.
    /// </summary>
    internal static readonly TimeSpan DefaultDonorLockPatience = TimeSpan.FromSeconds(2);

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
        bool alreadyCached = JbCacheGenerations.FindFor(config.CacheHome, config.SolutionPath).Owned.Count > 0;
        if (alreadyCached) return false;

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
    ///     Copy the donor's tree into place. Built somewhere <c>jb</c> and this server's own reset both ignore
    ///     and moved into position at the end, so a copy that fails or is cancelled leaves no directory a
    ///     later run could open as a cache: within one parent the move is a rename, which cannot be observed
    ///     half-done.
    /// </summary>
    private static bool Copy(string donorPath, string targetPath, string solutionPath, CancellationToken cancellationToken)
    {
        string inProgressPath = targetPath + InProgressSuffix;

        try
        {
            // A copy this one abandoned, from a process that was killed mid-run. Deletable because the caller
            // holds the target's lease, so nothing else can be building it.
            if (Directory.Exists(inProgressPath)) Directory.Delete(inProgressPath, true);

            CopyTree(donorPath, inProgressPath, cancellationToken);
            Directory.Move(inProgressPath, targetPath);

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