namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     What a command name resolves to on <c>PATH</c>, spelled once for every caller that needs the file
///     rather than the name.
/// </summary>
internal static class PathSearch
{
    /// <summary>
    ///     The variable this searches. Read through <see cref="Infrastructure.IEnvironment" />, so no test has
    ///     to touch the real one.
    /// </summary>
    internal const string PathVariable = "PATH";

    /// <summary>
    ///     The file a spawn of <paramref name="fileName" /> would start: the name itself when it carries a
    ///     directory separator, otherwise the first executable of that name across
    ///     <paramref name="pathVariable" />'s entries.
    /// </summary>
    /// <remarks>
    ///     <see langword="null" /> means "nothing to run". Outside Windows this is <c>execvp</c>'s rule, and
    ///     "executable" means a file with an execute bit. On Windows a name with no extension is looked up as
    ///     <c>name.exe</c>, which is <c>CreateProcess</c>'s rule, and any file of that name counts, there being
    ///     no execute bit to ask. <c>CreateProcess</c> also looks in the application's directory, the working directory
    ///     and the system directories before <c>PATH</c>. This answers for the <c>PATH</c> route alone.
    /// </remarks>
    internal static string? Resolve(string fileName, string? pathVariable)
    {
        string name = OperatingSystem.IsWindows() && !Path.HasExtension(fileName) ? fileName + ".exe" : fileName;

        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            return IsExecutableFile(name) ? name : null;

        if (string.IsNullOrEmpty(pathVariable)) return null;

        foreach (string directory in pathVariable.Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;

            string candidate = Path.Combine(directory, name);
            if (IsExecutableFile(candidate)) return candidate;
        }

        return null;
    }

    private static bool IsExecutableFile(string candidate)
    {
        try
        {
            if (!File.Exists(candidate)) return false;

            if (OperatingSystem.IsWindows()) return true;

            UnixFileMode mode = File.GetUnixFileMode(candidate);

            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            return false;
        }
    }
}