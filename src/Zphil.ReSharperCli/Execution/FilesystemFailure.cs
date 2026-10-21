namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Defines, as one predicate, the exceptions an ordinary filesystem mishap raises: I/O, permissions, and an
///     unsupported or invalid path.
/// </summary>
/// <remarks>
///     One predicate, so every site that swallows these swallows the same set. It is broader than the filters
///     spelled out on site, which separate contention from breakage or want a narrower set, so a filter that
///     does not call this is narrower on purpose.
/// </remarks>
internal static class FilesystemFailure
{
    internal static bool Covers(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;
    }
}