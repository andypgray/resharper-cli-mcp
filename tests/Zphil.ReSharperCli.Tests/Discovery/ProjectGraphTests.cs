using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Discovery;

public sealed class ProjectGraphTests : IDisposable
{
    private readonly FakeEnvironment _environment = new();
    private readonly string _root;

    public ProjectGraphTests()
    {
        _root = _environment.CreateTempDirectory();
    }

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public void Closure_TransitiveReferencesWithACycle_ReachesEachOnceInSolutionOrder()
    {
        // Arrange — A → B → C → B, with D and E unreachable from A and one reference outside the solution.
        SolutionFiles.PlantProject(_root, "A/A.csproj", @"..\B\B.csproj", @"..\..\Elsewhere\X.csproj");
        SolutionFiles.PlantProject(_root, "B/B.csproj", @"..\C\C.csproj");
        SolutionFiles.PlantProject(_root, "C/C.csproj", @"..\B\B.csproj");
        SolutionFiles.PlantProject(_root, "D/D.csproj", @"..\A\A.csproj");
        SolutionFiles.PlantProject(_root, "E/E.csproj");
        IReadOnlyList<SolutionProject> solution = Solution("E/E.csproj", "C/C.csproj", "A/A.csproj", "D/D.csproj", "B/B.csproj");

        // Act
        ModelReading<IReadOnlyList<SolutionProject>> closure = ProjectGraph.Closure(solution, [solution[2]], _root);

        // Assert
        ShouldHaveClosed(closure).ShouldBe(["C/C.csproj", "A/A.csproj", "B/B.csproj"]);
    }

    [Fact]
    public void Closure_ReferenceDeclaredInADirectoryBuildFile_ResolvesAgainstTheProjectAsMSBuildDoes()
    {
        // Arrange — a relative item path resolves against the project that evaluates it, not the file declaring it,
        // so from src/A this names src/Shared, where resolving against src/ would name a project that does not exist.
        SolutionFiles.PlantProject(_root, "src/A/A.csproj");
        SolutionFiles.PlantProject(_root, "src/Shared/Shared.csproj");
        SolutionFiles.PlantProject(_root, "src/Other/Other.csproj");
        SolutionFiles.Plant(_root, "src/Directory.Build.props", ProjectWith("""<ProjectReference Include="..\Shared\Shared.csproj" />"""));
        IReadOnlyList<SolutionProject> solution = Solution("src/A/A.csproj", "src/Shared/Shared.csproj", "src/Other/Other.csproj");

        // Act
        ModelReading<IReadOnlyList<SolutionProject>> closure = ProjectGraph.Closure(solution, [solution[0]], _root);

        // Assert
        ShouldHaveClosed(closure).ShouldBe(["src/A/A.csproj", "src/Shared/Shared.csproj"]);
    }

    [Fact]
    public void Closure_ReferenceInAnImportedFile_IsFollowed()
    {
        // Arrange — the import is spelt from the importing file's own directory, and so is the reference in it.
        SolutionFiles.Plant(
            _root,
            "src/A/A.csproj",
            FileWith("""<Import Project="$(MSBuildThisFileDirectory)..\..\build\refs.props" />"""));
        SolutionFiles.Plant(
            _root,
            "build/refs.props",
            ProjectWith("""<ProjectReference Include="$(MSBuildThisFileDirectory)..\src\B\B.csproj" />"""));
        SolutionFiles.PlantProject(_root, "src/B/B.csproj");
        SolutionFiles.PlantProject(_root, "src/C/C.csproj");
        IReadOnlyList<SolutionProject> solution = Solution("src/A/A.csproj", "src/B/B.csproj", "src/C/C.csproj");

        // Act
        ModelReading<IReadOnlyList<SolutionProject>> closure = ProjectGraph.Closure(solution, [solution[0]], _root);

        // Assert
        ShouldHaveClosed(closure).ShouldBe(["src/A/A.csproj", "src/B/B.csproj"]);
    }

    [Fact]
    public void Closure_ImportThroughAPropertyADirectoryBuildFileDefines_IsFollowed()
    {
        // Arrange — the project is read before the file defining the property it imports through.
        SolutionFiles.Plant(_root, "Directory.Build.props", FileWith("""<PropertyGroup><BuildDir>$(MSBuildThisFileDirectory)build\</BuildDir></PropertyGroup>"""));
        SolutionFiles.Plant(_root, "src/A/A.csproj", FileWith("""<Import Project="$(BuildDir)refs.props" />"""));
        SolutionFiles.Plant(_root, "build/refs.props", ProjectWith("""<ProjectReference Include="$(MSBuildThisFileDirectory)..\src\B\B.csproj" />"""));
        SolutionFiles.PlantProject(_root, "src/B/B.csproj");
        SolutionFiles.PlantProject(_root, "src/C/C.csproj");
        IReadOnlyList<SolutionProject> solution = Solution("src/A/A.csproj", "src/B/B.csproj", "src/C/C.csproj");

        // Act
        ModelReading<IReadOnlyList<SolutionProject>> closure = ProjectGraph.Closure(solution, [solution[0]], _root);

        // Assert
        ShouldHaveClosed(closure).ShouldBe(["src/A/A.csproj", "src/B/B.csproj"]);
    }

    [Fact]
    public void Closure_ImportThroughAPropertyAnImportedFileDefines_IsFollowed()
    {
        // Arrange — the import that needs the property comes first, so it resolves only once the second is read.
        SolutionFiles.Plant(
            _root,
            "src/A/A.csproj",
            FileWith("""
                     <Import Project="$(RefsDir)refs.props" />
                     <Import Project="$(MSBuildThisFileDirectory)..\..\build\paths.props" />
                     """));
        SolutionFiles.Plant(_root, "build/paths.props", FileWith("""<PropertyGroup><RefsDir>$(MSBuildThisFileDirectory)refs\</RefsDir></PropertyGroup>"""));
        SolutionFiles.Plant(_root, "build/refs/refs.props", ProjectWith("""<ProjectReference Include="$(SolutionDir)src\B\B.csproj" />"""));
        SolutionFiles.PlantProject(_root, "src/B/B.csproj");
        SolutionFiles.PlantProject(_root, "src/C/C.csproj");
        IReadOnlyList<SolutionProject> solution = Solution("src/A/A.csproj", "src/B/B.csproj", "src/C/C.csproj");

        // Act
        ModelReading<IReadOnlyList<SolutionProject>> closure = ProjectGraph.Closure(solution, [solution[0]], _root);

        // Assert
        ShouldHaveClosed(closure).ShouldBe(["src/A/A.csproj", "src/B/B.csproj"]);
    }

    [Theory]
    [InlineData("A/A.csproj", """<Import Project="$(NuGetPackageRoot)pkg/1.0.0/build/Pkg.targets" />""")]
    [InlineData("A/A.csproj", """<Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />""")]
    [InlineData(
        "Directory.Build.targets",
        """
        <PropertyGroup Condition="'$(ByDownload)' == 'true'"><PkgDir>$(NuGetPackageRoot)pkg/$(PkgVersion)/</PkgDir></PropertyGroup>
        <Import Project="$(PkgDir)buildTransitive/Pkg.targets" Condition="Exists('$(PkgDir)buildTransitive/Pkg.targets')" />
        """)]
    [InlineData(
        "Directory.Build.props",
        """<Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />""")]
    public void Closure_ImportFromAPackageOrToolsetFolder_IsLeftUnfollowed(string file, string body)
    {
        // Arrange — the project file is planted first, so a row naming it replaces it.
        SolutionFiles.PlantProject(_root, "A/A.csproj");
        SolutionFiles.PlantProject(_root, "B/B.csproj");
        SolutionFiles.Plant(_root, file, FileWith(body));
        IReadOnlyList<SolutionProject> solution = Solution("A/A.csproj", "B/B.csproj");

        // Act
        ModelReading<IReadOnlyList<SolutionProject>> closure = ProjectGraph.Closure(solution, [solution[0]], _root);

        // Assert
        ShouldHaveClosed(closure).ShouldBe(["A/A.csproj"]);
    }

    [Theory]
    [InlineData("""<Import Project="$(PackageRoot)build/Package.targets" Condition="Exists('$(PackageRoot)')" />""", "$(PackageRoot)build/Package.targets")]
    [InlineData(
        """<Import Project="$([MSBuild]::GetDirectoryNameOfFileAbove('$(MSBuildThisFileDirectory)', 'global.json'))/eng/refs.props" />""",
        "$([MSBuild]::GetDirectoryNameOfFileAbove('$(MSBuildThisFileDirectory)', 'global.json'))/eng/refs.props")]
    [InlineData(
        """<Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)'))" />""",
        "$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)'))")]
    [InlineData("""<PropertyGroup><Loop>$(Loop)x/</Loop></PropertyGroup><Import Project="$(Loop)refs.props" />""", "$(Loop)refs.props")]
    public void Closure_ImportThatCouldHideAReference_DeclinesNamingTheFileAndTheImport(string body, string project)
    {
        // Arrange — declared in a file the project imports, which the Directory.Build walk does not reach.
        SolutionFiles.Plant(_root, "A/A.csproj", FileWith("""<Import Project="$(MSBuildThisFileDirectory)..\build\extra.props" />"""));
        string declaring = Path.GetFullPath(SolutionFiles.Plant(_root, "build/extra.props", FileWith(body)));
        SolutionFiles.PlantProject(_root, "B/B.csproj");
        IReadOnlyList<SolutionProject> solution = Solution("A/A.csproj", "B/B.csproj");

        // Act
        ModelReading<IReadOnlyList<SolutionProject>> closure = ProjectGraph.Closure(solution, [solution[0]], _root);

        // Assert
        closure.ShouldHaveDeclined().ShouldBe($"{declaring} imports \"{project}\", which needs MSBuild to evaluate");
    }

    [Theory]
    [InlineData("", """<ProjectReference Include="$(SolutionDir)B\B.csproj" />""", new[] { "A/A.csproj", "B/B.csproj" })]
    [InlineData(
        """<PropertyGroup><Shared>$(MSBuildThisFileDirectory)B\</Shared></PropertyGroup>""",
        """<ProjectReference Include="$(Shared)B.csproj" />""",
        new[] { "A/A.csproj", "B/B.csproj" })]
    [InlineData(
        """
        <PropertyGroup Condition="'$(Flavour)' == 'b'"><Dep>B</Dep></PropertyGroup>
        <PropertyGroup Condition="'$(Flavour)' == 'c'"><Dep>C</Dep></PropertyGroup>
        """,
        """<ProjectReference Include="..\$(Dep)\$(Dep).csproj" />""",
        new[] { "A/A.csproj", "B/B.csproj", "C/C.csproj" })]
    [InlineData(
        """<ItemGroup><ProjectReference Include="$(MSBuildProjectDirectory)\..\B\B.csproj" /></ItemGroup>""",
        "",
        new[] { "A/A.csproj", "B/B.csproj" })]
    public void Closure_ReferenceThroughAKnownProperty_ReachesEveryValue(string directoryBuildProps, string reference, string[] expected)
    {
        // Arrange — conditions are ignored, so a property given two values names two projects.
        if (directoryBuildProps.Length > 0) SolutionFiles.Plant(_root, "Directory.Build.props", FileWith(directoryBuildProps));
        SolutionFiles.Plant(_root, "A/A.csproj", ProjectWith(reference));
        SolutionFiles.PlantProject(_root, "B/B.csproj");
        SolutionFiles.PlantProject(_root, "C/C.csproj");
        SolutionFiles.PlantProject(_root, "D/D.csproj");
        IReadOnlyList<SolutionProject> solution = Solution("A/A.csproj", "B/B.csproj", "C/C.csproj", "D/D.csproj");

        // Act
        ModelReading<IReadOnlyList<SolutionProject>> closure = ProjectGraph.Closure(solution, [solution[0]], _root);

        // Assert
        ShouldHaveClosed(closure).ShouldBe(expected);
    }

    [Theory]
    [InlineData("A/A.csproj", """<ProjectReference Include="$(Root)/B/B.csproj" />""", "$(Root)/B/B.csproj")]
    [InlineData("A/A.csproj", """<ProjectReference Include="../*/*.csproj" />""", "../*/*.csproj")]
    [InlineData("Directory.Build.props", """<ProjectReference Include="@(Shared)" />""", "@(Shared)")]
    public void Closure_ReferenceThatNeedsEvaluating_DeclinesNamingTheFileAndTheEntry(string file, string body, string entry)
    {
        // Arrange — the project file is planted first, so a row naming it replaces it.
        SolutionFiles.PlantProject(_root, "A/A.csproj");
        SolutionFiles.PlantProject(_root, "B/B.csproj");
        string declaring = Path.GetFullPath(SolutionFiles.Plant(_root, file, ProjectWith(body)));
        IReadOnlyList<SolutionProject> solution = Solution("A/A.csproj", "B/B.csproj");

        // Act
        ModelReading<IReadOnlyList<SolutionProject>> closure = ProjectGraph.Closure(solution, [solution[0]], _root);

        // Assert
        closure.ShouldHaveDeclined().ShouldBe($"{declaring} references a project as \"{entry}\", which needs MSBuild to evaluate");
    }

    [Fact]
    public void Closure_ProjectThatWillNotParse_DeclinesNamingIt()
    {
        // Arrange
        string broken = Path.GetFullPath(SolutionFiles.Plant(_root, "A/A.csproj", "<Project><ItemGroup>"));
        IReadOnlyList<SolutionProject> solution = Solution("A/A.csproj");

        // Act
        ModelReading<IReadOnlyList<SolutionProject>> closure = ProjectGraph.Closure(solution, [solution[0]], _root);

        // Assert
        closure.ShouldHaveDeclined().ShouldStartWith($"{broken} could not be read (");
    }

    private static string ProjectWith(string body)
    {
        return FileWith($"<ItemGroup>{body}</ItemGroup>");
    }

    private static string FileWith(string body)
    {
        return $"""
                <Project>
                  {body}
                </Project>
                """;
    }

    private IReadOnlyList<SolutionProject> Solution(params string[] spellings)
    {
        return spellings.Select(spelling => new SolutionProject(spelling, SolutionModel.ResolveSpelling(spelling, _root))).ToList();
    }

    /// <summary>
    ///     Returns the closure's projects as the solution spells them, failing with the reason it declined.
    /// </summary>
    private static List<string> ShouldHaveClosed(ModelReading<IReadOnlyList<SolutionProject>> closure)
    {
        return closure.ShouldHaveValue().Select(project => project.Spelling).ToList();
    }
}