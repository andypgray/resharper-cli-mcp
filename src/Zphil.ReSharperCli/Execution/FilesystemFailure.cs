namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     The exception shapes an ordinary filesystem mishap takes — I/O, permissions, an unsupported or
///     outright invalid path — as one predicate, so every cache-home side effect that swallows them swallows
///     the same set and a type added or dropped moves them all at once.
/// </summary>
/// <remarks>
///     Deliberately broader than the filters that stay spelled out on site: <see cref="JbRunLock" />'s open
///     paths, which separate contention from breakage, and the delete loops that want a genuinely narrower
///     set. A filter that does not call this is narrower <em>on purpose</em>, and now reads that way.
/// </remarks>
internal static class FilesystemFailure
{
    internal static bool Covers(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;
    }
}