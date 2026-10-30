namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>Plants the source, project and solution files a test's solution is made of.</summary>
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

    /// <summary>
    ///     Writes an SDK project at <paramref name="relativePath" /> under <paramref name="root" /> with a
    ///     <c>ProjectReference</c> to each of <paramref name="references" />, spelt as given.
    /// </summary>
    public static void PlantProject(string root, string relativePath, params string[] references)
    {
        IEnumerable<string> items = references.Select(reference => $"""    <ProjectReference Include="{reference}" />""");
        var content = $"""
                       <Project Sdk="Microsoft.NET.Sdk">
                         <ItemGroup>
                       {string.Join("\n", items)}
                         </ItemGroup>
                       </Project>
                       """;

        Plant(root, relativePath, content);
    }

    /// <summary>
    ///     Writes a <c>.slnx</c> called <paramref name="fileName" /> in <paramref name="root" /> listing
    ///     <paramref name="projects" /> as given, and returns its path.
    /// </summary>
    public static string PlantSlnx(string root, string fileName, params string[] projects)
    {
        IEnumerable<string> entries = projects.Select(project => $"""  <Project Path="{project}" />""");

        return Plant(root, fileName, $"<Solution>\n{string.Join("\n", entries)}\n</Solution>\n");
    }

    /// <summary>
    ///     Plants <c>App.slnx</c> in <paramref name="root" /> with project A referencing B, project C on its own,
    ///     and <c>A/Foo.cs</c>, and returns the solution's path: the smallest solution whose filter both follows a
    ///     reference and leaves a project out.
    /// </summary>
    public static string PlantThreeProjects(string root)
    {
        PlantProject(root, "A/A.csproj", "../B/B.csproj");
        PlantProject(root, "B/B.csproj");
        PlantProject(root, "C/C.csproj");
        Plant(root, "A/Foo.cs");

        return PlantSlnx(root, "App.slnx", "A/A.csproj", "B/B.csproj", "C/C.csproj");
    }
}