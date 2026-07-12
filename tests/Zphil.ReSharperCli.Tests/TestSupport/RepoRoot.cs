namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Locates the repository root — the directory holding <c>Zphil.ReSharperCli.slnx</c> — by walking
///     up from the test assembly's base directory. Lets documentation tests read the <em>source</em>
///     markdown tree directly, both locally and in a CI checkout (the <c>.slnx</c> always sits above
///     <c>bin/</c>), with no csproj <c>Content</c> copying. Mirrors the spirit of <see cref="Fixtures" />.
/// </summary>
internal static class RepoRoot
{
    private const string SolutionFileName = "Zphil.ReSharperCli.slnx";

    /// <summary>Absolute path to the repository root.</summary>
    public static string Location { get; } = Locate();

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