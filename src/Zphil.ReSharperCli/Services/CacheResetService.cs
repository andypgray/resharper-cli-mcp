using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Services;

/// <summary>A generation that could not be deleted, and what the filesystem said about it.</summary>
internal sealed record CacheResetFailure(string Name, string Reason);

/// <summary>
///     What a reset did: the generation directory names it dropped and the ones it could not, under the
///     cache home it looked in. Formatting lives in <c>CacheResetFormatter</c>.
/// </summary>
internal sealed record CacheResetOutcome(
    string SolutionPath,
    string CacheHome,
    IReadOnlyList<string> Dropped,
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
        string solutionName = Path.GetFileNameWithoutExtension(config.SolutionPath);

        using IDisposable runLease = await runLock.AcquireAsync(config.SolutionPath, config.CacheHome, cancellationToken);

        // Enumerated inside the lock, so the set found is the set deleted: outside it, a run starting in the
        // gap could fork a generation this call would then leave behind while reporting a clean reset.
        var generations = Find(config.CacheHome, solutionName);

        var hashes = generations.Select(generation => generation.Hash).Distinct(StringComparer.Ordinal).ToList();
        if (hashes.Count > 1) throw Ambiguous(config, solutionName, generations);

        List<string> dropped = [];
        List<CacheResetFailure> failures = [];
        foreach (JbCacheGeneration generation in generations)
            try
            {
                Directory.Delete(generation.FullPath, true);
                dropped.Add(generation.Name);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(new CacheResetFailure(generation.Name, SingleLine(exception.Message)));
            }

        // The marker claims a jb run against this generation succeeded recently, which is what stops the next
        // session pre-warming. After a reset that claim is false however the deletes went, and the marker's
        // one safe failure direction is a redundant pre-warm.
        JbWarmMarker.Clear(config.SolutionPath, config.CacheHome);

        return new CacheResetOutcome(config.SolutionPath, config.CacheHome, dropped, failures);
    }

    /// <summary>
    ///     <see cref="JbCacheGenerations.Find" /> with its enumeration failures turned into a reportable
    ///     error. A cache home this server cannot read is not "nothing to drop" — answering a delete request
    ///     with a clean report would be the worst possible reading of it.
    /// </summary>
    private static List<JbCacheGeneration> Find(string cacheHome, string solutionName)
    {
        try
        {
            return JbCacheGenerations.Find(cacheHome, solutionName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new UserErrorException(
                $"Could not read the ReSharper cache home \"{cacheHome}\" ({SingleLine(exception.Message)}).", exception);
        }
    }

    /// <summary>
    ///     Two solutions with the same file name share a cache home, and their generation directories differ
    ///     only by a hash that records no path. Nothing here can tell them apart, and this call deletes what
    ///     it picks, so it names the candidates and refuses.
    /// </summary>
    private static UserErrorException Ambiguous(
        ResolvedConfig config,
        string solutionName,
        List<JbCacheGeneration> generations)
    {
        string candidates = string.Join("\n", generations.Select(generation => $"  - {generation.Name}"));

        return new UserErrorException(
            $"The ReSharper cache home \"{config.CacheHome}\" holds generations for more than one solution file named \"{solutionName}\":\n"
            + candidates + "\n"
            + $"jb's directory names record a hash of the solution path, not the path, so which of these belongs to \"{config.SolutionPath}\" cannot be determined here.\n"
            + "Delete the right one yourself, or point JB_CACHE_HOME at a cache home this solution does not share.");
    }

    /// <summary>
    ///     Flattens an exception message onto one line, so a reported reason cannot break out of the list item
    ///     it belongs to.
    /// </summary>
    private static string SingleLine(string reason)
    {
        return reason.ReplaceLineEndings(" ").Trim();
    }
}