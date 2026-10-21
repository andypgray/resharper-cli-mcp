using Microsoft.Extensions.Logging;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Provides the cold tombstone: a zero-byte file beside the warm marker recording that this solution's
///     cache was dropped on purpose, and that the next <c>jb</c> run against it is meant to be cold.
/// </summary>
/// <remarks>
///     <para>
///         Written by a cache reset and cleared by the first run that succeeds afterwards. Without it, seeding
///         a cold generation by copying a warm sibling's would undo a reset silently and at once, handing back
///         the index the user asked to be rid of. An absent cache and an emptied one look identical on disk, so
///         the intention has to be recorded, and it has to outlive the process that recorded it.
///     </para>
///     <para>
///         It fails towards rebuilding rather than seeding, because refusing to seed a cache is free and
///         undoing a reset is not.
///     </para>
/// </remarks>
internal static class JbColdTombstone
{
    private const string Extension = "cold";

    internal static string PathFor(string solutionPath, string cacheHome)
    {
        return JbSidecar.PathFor(solutionPath, cacheHome, Extension);
    }

    /// <summary>
    ///     Records that this solution's cache has just been dropped on purpose.
    /// </summary>
    /// <remarks>
    ///     The content carries nothing; existence is the whole statement. A failure to write is a warning rather
    ///     than a <c>Debug</c> line, because it leaves a promise to the user unenforced.
    /// </remarks>
    internal static void Write(string solutionPath, string cacheHome, ILogger logger)
    {
        try
        {
            using FileStream tombstone = JbSidecar.OpenToWrite(solutionPath, cacheHome, Extension);
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            logger.LogWarning(
                exception,
                "Could not record that the cache for solution {SolutionPath} in cache home {CacheHome} was reset; a later run may seed it from another copy of this solution instead of rebuilding it",
                solutionPath,
                cacheHome);
        }
    }

    /// <summary>
    ///     Determines whether the last thing to happen to this solution's cache was a reset.
    /// </summary>
    /// <remarks>
    ///     Anything that goes wrong answers <see langword="true" />: the caller's only use for a
    ///     <see langword="false" /> is to start copying, and it must not do that on a question this could not
    ///     answer.
    /// </remarks>
    internal static bool Exists(string solutionPath, string cacheHome, ILogger logger)
    {
        try
        {
            return File.Exists(PathFor(solutionPath, cacheHome));
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            logger.LogDebug(exception, "Could not read the cache reset record for solution {SolutionPath} in cache home {CacheHome}", solutionPath, cacheHome);
            return true;
        }
    }

    /// <summary>
    ///     Discharges the promise, once a run has rebuilt the cache or a reclaim has left no checkout to keep
    ///     cold.
    /// </summary>
    /// <remarks>
    ///     A <c>jb</c> run that succeeds after the reset rebuilt the cache as this solution's own, so there is
    ///     nothing left to protect. A reset reclaiming the cache of a checkout that no longer exists has no
    ///     next run to keep cold, and a tombstone left there would deny the seeding to whatever is created at
    ///     that path later. Failing to clear costs a later optimisation and nothing else, the safe direction, so
    ///     it is logged no louder than <c>Debug</c>.
    /// </remarks>
    internal static void Clear(string solutionPath, string cacheHome, ILogger logger)
    {
        JbSidecar.TryDelete(solutionPath, cacheHome, Extension, "cache reset record", logger);
    }
}