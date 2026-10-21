namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Resolves a command name to the file a spawn of it would start, searching <c>PATH</c> for a bare name.
/// </summary>
internal static class PathSearch
{
    internal const string PathVariable = "PATH";

    /// <summary>
    ///     Returns the file a spawn of <paramref name="fileName" /> would start: the name itself when it carries
    ///     a directory separator, otherwise the first executable of that name across
    ///     <paramref name="pathVariable" />'s entries.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see langword="null" /> means "nothing to run". Outside Windows this is <c>execvp</c>'s rule, and
    ///         "executable" means a file with an execute bit. On Windows a name with no extension is looked up as
    ///         <c>name.exe</c>, which is <c>CreateProcess</c>'s rule, and any file of that name counts, there
    ///         being no execute bit to ask. <c>CreateProcess</c> also looks in the application's directory, the
    ///         working directory and the system directories before <c>PATH</c>. This answers for the <c>PATH</c>
    ///         route alone.
    ///     </para>
    ///     <para>
    ///         <paramref name="pathVariable" /> is the variable's value, passed in rather than read here so that
    ///         no test has to touch the real environment.
    ///     </para>
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