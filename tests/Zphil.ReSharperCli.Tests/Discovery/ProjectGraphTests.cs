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
        ModelReading<IReadOnlyList<SolutionProject>> closure = ProjectGraph.Closure(solution, [solution[2]]);

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
        ModelReading<IReadOnlyList<SolutionProject>> closure = ProjectGraph.Closure(solution, [solution[0]]);

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
            ProjectWith("""<Import Project="$(MSBuildThisFileDirectory)..\..\build\refs.props" />"""));
        SolutionFiles.Plant(
            _root,
            "build/refs.props",
            ProjectWith("""<ProjectReference Include="$(MSBuildThisFileDirectory)..\src\B\B.csproj" />"""));
        SolutionFiles.PlantProject(_root, "src/B/B.csproj");
        SolutionFiles.PlantProject(_root, "src/C/C.csproj");
        IReadOnlyList<SolutionProject> solution = Solution("src/A/A.csproj", "src/B/B.csproj", "src/C/C.csproj");

        // Act
        ModelReading<IReadOnlyList<SolutionProject>> closure = ProjectGraph.Closure(solution, [solution[0]]);

        // Assert
        ShouldHaveClosed(closure).ShouldBe(["src/A/A.csproj", "src/B/B.csproj"]);
    }

    [Fact]
    public void Closure_ImportThatNeedsAnotherProperty_IsLeftUnfollowed()
    {
        // Arrange — the shape of a package's targets wired by hand, which declares no project references.
        SolutionFiles.Plant(
            _root,
            "src/A/A.csproj",
            ProjectWith("""<Import Project="$(PackageRoot)build/Package.targets" Condition="Exists('$(PackageRoot)')" />"""));
        SolutionFiles.PlantProject(_root, "src/B/B.csproj");
        IReadOnlyList<SolutionProject> solution = Solution("src/A/A.csproj", "src/B/B.csproj");

        // Act
        ModelReading<IReadOnlyList<SolutionProject>> closure = ProjectGraph.Closure(solution, [solution[0]]);

        // Assert
        ShouldHaveClosed(closure).ShouldBe(["src/A/A.csproj"]);
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
        ModelReading<IReadOnlyList<SolutionProject>> closure = ProjectGraph.Closure(solution, [solution[0]]);

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
        ModelReading<IReadOnlyList<SolutionProject>> closure = ProjectGraph.Closure(solution, [solution[0]]);

        // Assert
        closure.ShouldHaveDeclined().ShouldStartWith($"{broken} could not be read (");
    }

    private static string ProjectWith(string body)
    {
        return $"""
                <Project>
                  <ItemGroup>
                    {body}
                  </ItemGroup>
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