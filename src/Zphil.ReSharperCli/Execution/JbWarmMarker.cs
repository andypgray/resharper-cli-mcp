using Serilog;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     A zero-byte file inside the cache home whose modification time records when a <c>jb</c> run against
///     that cache generation last <em>succeeded</em>. The speculative pre-warm reads it to skip a generation
///     something has already warmed; every successful run through <see cref="Services.JbRunner" /> — a
///     foreground tool call included — stamps it.
/// </summary>
/// <remarks>
///     <para>
///         Deliberately not the <see cref="JbRunLock" /> lock file's own timestamp, even though the two sit
///         side by side under the same key. The lock file's mtime moves when a run <em>starts</em> and again
///         when one <em>fails</em>, while the debounce needs "when did one last succeed"; a lock file held
///         <see cref="FileShare.None" /> cannot be stamped from a second handle anyway; and the lock is
///         load-bearing correctness where this is only a hint, so a bug here must not be able to break it.
///         The coupling runs one way — this reuses <see cref="JbRunLock.ComputeKey" />, and
///         <see cref="JbRunLock" /> never references the marker.
///     </para>
///     <para>
///         Every filesystem failure is swallowed, because a marker that cannot be written or read is not a
///         reason to fail — or to log against — a run the user asked for. Every failure mode also reads as
///         "not warm": a missing file, an unreadable one, and a future-dated one all report stale. So the
///         marker can only ever permit a redundant pre-warm, never permanently suppress one.
///     </para>
/// </remarks>
internal static class JbWarmMarker
{
    /// <summary>
    ///     Where the marker for one cache generation lives: beside its lock file, in the cache home itself
    ///     and under the same key, because the cache home <em>is</em> the shared resource.
    /// </summary>
    internal static string PathFor(string solutionPath, string cacheHome)
    {
        string key = JbRunLock.ComputeKey(solutionPath, cacheHome);
        return JbRunLock.SidecarPathFor(cacheHome, key, "warm");
    }

    /// <summary>
    ///     Record that a <c>jb</c> run against this cache generation has just succeeded. The file's content
    ///     carries nothing — closing an empty <see cref="FileMode.Create" /> handle flushes the modification
    ///     time, which is the whole payload.
    /// </summary>
    internal static void Stamp(string solutionPath, string cacheHome)
    {
        try
        {
            // FileShare.ReadWrite: a concurrent server stamping or reading the same generation must not see
            // a sharing violation for what is only a hint.
            using FileStream marker = new(PathFor(solutionPath, cacheHome), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Log.Debug(exception, "Could not stamp the jb warm marker for solution {SolutionPath} in cache home {CacheHome}", solutionPath, cacheHome);
        }
    }

    /// <summary>
    ///     Whether a run against this cache generation succeeded within <paramref name="window" />. A missing
    ///     marker reads as stale for free: <see cref="File.GetLastWriteTimeUtc(string)" /> returns 1601-01-01
    ///     rather than throwing. A future-dated one — a moved clock, or a cache home copied between machines —
    ///     also reads as stale, so it cannot suppress pre-warming forever.
    /// </summary>
    internal static bool IsFreshWithin(string solutionPath, string cacheHome, TimeSpan window)
    {
        try
        {
            TimeSpan age = DateTime.UtcNow - File.GetLastWriteTimeUtc(PathFor(solutionPath, cacheHome));
            return age >= TimeSpan.Zero && age < window;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Log.Debug(exception, "Could not read the jb warm marker for solution {SolutionPath} in cache home {CacheHome}", solutionPath, cacheHome);
            return false;
        }
    }
}