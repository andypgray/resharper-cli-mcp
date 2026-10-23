using System.Diagnostics;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Locates the repository root, the directory holding <c>Zphil.ReSharperCli.slnx</c>, by walking up from
///     the test assembly's base directory.
/// </summary>
/// <remarks>
///     A test reads the source tree directly, both locally and in a CI checkout (the <c>.slnx</c> always sits
///     above <c>bin/</c>), with no csproj <c>Content</c> copying.
/// </remarks>
internal static class RepoRoot
{
    /// <summary>
    ///     The server's project file, relative to the root: where its version, icon and description are declared.
    /// </summary>
    public const string ProductCsproj = "src/Zphil.ReSharperCli/Zphil.ReSharperCli.csproj";

    private const string SolutionFileName = "Zphil.ReSharperCli.slnx";

    /// <summary>Gets the absolute path to the repository root.</summary>
    public static string Location { get; } = Locate();

    /// <summary>Reads the text of the file at <paramref name="segments" />, relative to the root.</summary>
    public static string ReadText(params string[] segments)
    {
        string[] path = [Location, .. segments];
        return File.ReadAllText(Path.Combine(path));
    }

    /// <summary>
    ///     Returns the absolute path of every file git tracks under the root whose path matches the git pathspec
    ///     <paramref name="pattern" />, in ordinal order: the set a CI checkout holds, rather than whatever build
    ///     output, release staging or gitignored local files the working tree has beside it.
    /// </summary>
    /// <remarks>
    ///     Asks <c>git ls-files</c>, so it needs git on <c>PATH</c> and a checkout rather than a source archive,
    ///     and throws with git's own error when either is missing. A tracked file deleted from the working tree
    ///     is left out, because a caller reads every path it is given.
    /// </remarks>
    public static IReadOnlyList<string> EnumerateCommitted(string pattern)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = Location,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in new[] { "ls-files", "-z", "--", pattern }) startInfo.ArgumentList.Add(argument);

        using Process git = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("git ls-files could not be started.");
        Task<string> standardError = git.StandardError.ReadToEndAsync();
        string standardOutput = git.StandardOutput.ReadToEnd();
        git.WaitForExit();

        if (git.ExitCode != 0)
            throw new InvalidOperationException(
                $"git ls-files -- {pattern} exited {git.ExitCode} in {Location}: {standardError.Result.Trim()}");

        return standardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(relativePath => Path.GetFullPath(Path.Combine(Location, relativePath)))
            .Where(File.Exists)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private static string Locate()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
                return directory.FullName;

        throw new InvalidOperationException(
            $"Could not locate {SolutionFileName} walking up from {AppContext.BaseDirectory}.");
    }
}