namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Locates the repository root — the directory holding <c>Zphil.ReSharperCli.slnx</c> — by walking
///     up from the test assembly's base directory. Lets documentation tests read the <em>source</em>
///     markdown tree directly, both locally and in a CI checkout (the <c>.slnx</c> always sits above
///     <c>bin/</c>), with no csproj <c>Content</c> copying. Mirrors the spirit of <see cref="Fixtures" />.
/// </summary>
internal static class RepoRoot
{
    /// <summary>The server's project file, relative to the root — where its version, icon and description are declared.</summary>
    public const string ProductCsproj = "src/Zphil.ReSharperCli/Zphil.ReSharperCli.csproj";

    private const string SolutionFileName = "Zphil.ReSharperCli.slnx";

    /// <summary>
    ///     Directory names whose contents are never committed: build output, the release staging tree, and
    ///     git's own store. Matched case-insensitively, as a case-insensitive file system would.
    /// </summary>
    private static readonly string[] UncommittedDirectories = ["bin", "obj", "artifacts", ".git"];

    /// <summary>Absolute path to the repository root.</summary>
    public static string Location { get; } = Locate();

    /// <summary>The text of the file at <paramref name="segments" />, relative to the root.</summary>
    public static string ReadText(params string[] segments)
    {
        string[] path = [Location, .. segments];
        return File.ReadAllText(Path.Combine(path));
    }

    /// <summary>
    ///     Every file under the root matching <paramref name="pattern" />, in ordinal order, outside the
    ///     directories that hold build output or git's store — the working tree's own files rather than copies
    ///     of them that a build or a release stage left behind.
    /// </summary>
    public static IReadOnlyList<string> EnumerateCommitted(string pattern)
    {
        return Directory
            .EnumerateFiles(Location, pattern, SearchOption.AllDirectories)
            .Where(path => !IsUnderUncommittedDirectory(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsUnderUncommittedDirectory(string path)
    {
        string relativePath = Path.GetRelativePath(Location, path);
        string[] segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return segments.Any(segment => UncommittedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
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