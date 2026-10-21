namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>Plants the source files a test's solution is meant to contain.</summary>
/// <remarks>
///     Takes the root rather than an environment, so a file can be rooted at the working directory or at a
///     solution directory of the test's own.
/// </remarks>
internal static class SolutionFiles
{
    /// <summary>
    ///     Writes <paramref name="content" /> at <paramref name="relativePath" /> under <paramref name="root" />,
    ///     creating the directories on the way, and returns the file's full path.
    /// </summary>
    public static string Plant(string root, string relativePath, string content = "")
    {
        string fullPath = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }
}