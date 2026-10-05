namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     The exception shapes an ordinary filesystem mishap takes — I/O, permissions, an unsupported or
///     outright invalid path — as one predicate, so every site that swallows them swallows the same set and a
///     type added or dropped moves them all at once.
/// </summary>
/// <remarks>
///     Deliberately broader than the filters spelled out on site, which separate contention from breakage
///     or want a genuinely narrower set: a filter that does not call this is narrower <em>on purpose</em>.
/// </remarks>
internal static class FilesystemFailure
{
    internal static bool Covers(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;
    }
}