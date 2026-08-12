using Serilog;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     A zero-byte file beside the warm marker recording that this solution's cache was <em>deliberately</em>
///     dropped, and that the next <c>jb</c> run against it is meant to be cold. Written by a cache reset,
///     cleared by the first run that succeeds afterwards.
/// </summary>
/// <remarks>
///     <para>
///         Only one thing reads it, and only one thing needs it: seeding a cold generation by copying a warm
///         sibling's would otherwise undo a reset silently and immediately, handing back the very index the
///         user asked to be rid of. An absent cache and an emptied one look identical on disk, so the
///         intention has to be recorded somewhere, and it has to outlive the process that recorded it.
///     </para>
///     <para>
///         Its failure direction is the opposite of <see cref="JbWarmMarker" />'s, and deliberately so. The
///         marker may only ever fail towards redundant work; this may only ever fail towards <em>less</em>
///         work being skipped. So a tombstone that cannot be written is a warning rather than a debug line —
///         it leaves a promise to the user unenforced — and a key that cannot even be derived reads as
///         <see cref="Exists" />, because refusing to seed a cache is free and undoing a reset is not.
///     </para>
/// </remarks>
internal static class JbColdTombstone
{
    /// <summary>
    ///     Where the tombstone for one cache generation lives: the cache home, under the same key as the lock
    ///     file and the warm marker, so all three move together if the scheme ever changes.
    /// </summary>
    internal static string PathFor(string solutionPath, string cacheHome)
    {
        string key = JbRunLock.ComputeKey(solutionPath, cacheHome);
        return JbRunLock.SidecarPathFor(cacheHome, key, "cold");
    }

    /// <summary>
    ///     Record that this solution's cache has just been dropped on purpose. The content carries nothing;
    ///     existence is the whole statement.
    /// </summary>
    internal static void Write(string solutionPath, string cacheHome)
    {
        try
        {
            using FileStream tombstone = new(PathFor(solutionPath, cacheHome), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Log.Warning(
                exception,
                "Could not record that the cache for solution {SolutionPath} in cache home {CacheHome} was reset; a later run may seed it from another copy of this solution instead of rebuilding it",
                solutionPath,
                cacheHome);
        }
    }

    /// <summary>
    ///     Whether the last thing to happen to this solution's cache was a reset. Anything that goes wrong
    ///     answers <see langword="true" />: the caller's only use for a <see langword="false" /> is to start
    ///     copying, and it must not do that on a question this could not answer.
    /// </summary>
    internal static bool Exists(string solutionPath, string cacheHome)
    {
        try
        {
            return File.Exists(PathFor(solutionPath, cacheHome));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Log.Debug(exception, "Could not read the cache reset record for solution {SolutionPath} in cache home {CacheHome}", solutionPath, cacheHome);
            return true;
        }
    }

    /// <summary>
    ///     Discharge the promise: a <c>jb</c> run has succeeded since the reset, so the cache it rebuilt is
    ///     this solution's own and there is nothing left to protect. Failing to clear it costs a later
    ///     optimisation and nothing else, which is the safe direction, so it goes no louder than debug.
    /// </summary>
    internal static void Clear(string solutionPath, string cacheHome)
    {
        try
        {
            File.Delete(PathFor(solutionPath, cacheHome));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Log.Debug(exception, "Could not clear the cache reset record for solution {SolutionPath} in cache home {CacheHome}", solutionPath, cacheHome);
        }
    }
}