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
///         What belongs to this solution is settled by <see cref="JbSolutionCacheHash" /> rather than by the
///         generation's file name, which several checkouts of one repository share. A generation is deleted
///         only where the hash in its name is the one this solution's path produces; everything else is
///         reported as left alone, and a computed hash matching nothing deletes nothing at all. That is what
///         makes a shared cache home ordinary rather than an obstacle — before, two checkouts in one cache
///         home made the tool refuse for both.
///     </para>
///     <para>
///         A failed delete is reported, not thrown, and may leave a generation partly deleted. That is
///         deliberate: a directory jb rebuilds is a better outcome than a call that fails after deleting most
///         of one, this tool is idempotent so re-running finishes the job, and the one thing that reliably
///         holds these files open — another <c>jb</c> — is named in the report.
///     </para>
/// </remarks>
internal sealed class CacheResetService(JbRunLock runLock)
{
    public async Task<CacheResetOutcome> RunAsync(ResolvedConfig config, CancellationToken cancellationToken)
    {
        using IDisposable runLease = await runLock.AcquireAsync(config.SolutionPath, config.CacheHome, cancellationToken);

        // Enumerated inside the lock, so the set found is the set deleted: outside it, a run starting in the
        // gap could fork a generation this call would then leave behind while reporting a clean reset. Which
        // of the same-named generations are this solution's own is FindFor's proof; this only decides what
        // happens to each half.
        JbSolutionGenerations generations = Find(config.CacheHome, config.SolutionPath);
        var leftAlone = generations.Neighbours.Select(generation => generation.Name).ToList();

        List<string> dropped = [];
        List<CacheResetFailure> failures = [];
        foreach (JbCacheGeneration generation in generations.Owned)
            try
            {
                Directory.Delete(generation.FullPath, true);
                dropped.Add(generation.Name);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(new CacheResetFailure(generation.Name, exception.Message));
            }

        // The marker claims a jb run against this generation succeeded recently, which is what stops the next
        // session pre-warming. After a reset that claim is false however the deletes went, and the marker's
        // one safe failure direction is a redundant pre-warm. Clearing it also withdraws this solution as a
        // donor for anything else, which is the same statement pointed outwards.
        JbWarmMarker.Clear(config.SolutionPath, config.CacheHome);

        // Unconditional, including the reset that dropped nothing: what the caller asked for is a cold next
        // run, and the one mechanism that could quietly supply a warm cache instead has to be told.
        JbColdTombstone.Write(config.SolutionPath, config.CacheHome);

        return new CacheResetOutcome(config.SolutionPath, config.CacheHome, dropped, leftAlone, failures);
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