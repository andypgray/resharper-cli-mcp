using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Discovery;

public sealed class SolutionModelTests : IDisposable
{
    private readonly FakeEnvironment _environment = new();
    private readonly string _root;

    public SolutionModelTests()
    {
        _root = _environment.CreateTempDirectory();
    }

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public void ReadProjects_Sln_ListsProjectsAndSkipsSolutionFolders()
    {
        // Arrange — a solution folder is a Project line too, with its name where a project's path would be.
        string solution = SolutionFiles.Plant(
            _root,
            "App.sln",
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "src", "src", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "A", "src\A\A.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Project("{F2A71F9B-5D33-465A-A702-920D77279786}") = "B", "src\B\B.fsproj", "{33333333-3333-3333-3333-333333333333}"
            EndProject
            """);

        // Act
        ModelReading<IReadOnlyList<SolutionProject>> reading = SolutionModel.ReadProjects(solution);

        // Assert — the spelling is kept as written, because a filter repeats it.
        reading.ShouldHaveValue().ShouldBe(
        [
            new SolutionProject(@"src\A\A.csproj", Path.Combine(_root, "src", "A", "A.csproj")),
            new SolutionProject(@"src\B\B.fsproj", Path.Combine(_root, "src", "B", "B.fsproj"))
        ]);
    }

    [Fact]
    public void ReadProjects_SlnxWithNestedFolders_ListsEveryProjectInOrder()
    {
        // Arrange
        string solution = SolutionFiles.Plant(
            _root,
            "App.slnx",
            """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/A/A.csproj" />
                <Folder Name="/src/inner/">
                  <Project Path="src/inner/B/B.csproj" />
                </Folder>
              </Folder>
              <Project Path="C/C.csproj" />
            </Solution>
            """);

        // Act
        ModelReading<IReadOnlyList<SolutionProject>> reading = SolutionModel.ReadProjects(solution);

        // Assert
        reading.ShouldHaveValue().Select(project => project.Spelling).ShouldBe(["src/A/A.csproj", "src/inner/B/B.csproj", "C/C.csproj"]);
    }

    [Theory]
    [InlineData("App.slnx", "<Solution><Project Path=", "the solution file could not be read")]
    [InlineData("App.slnx", "<Solution />", "the solution file lists no projects")]
    [InlineData("App.sln", "Project(\"{X}\") = \"A\", \"A\0\\A.csproj\", \"{Y}\"", "the solution file lists a project path that is not valid")]
    [InlineData("App.slnf", "{}", "App.slnf is neither a .sln nor a .slnx")]
    public void ReadProjects_UnusableSolution_DeclinesWithAReason(string fileName, string content, string reason)
    {
        // Arrange
        string solution = SolutionFiles.Plant(_root, fileName, content);

        // Act
        ModelReading<IReadOnlyList<SolutionProject>> reading = SolutionModel.ReadProjects(solution);

        // Assert
        reading.ShouldHaveDeclined().ShouldStartWith(reason);
    }
}