using System.Text.Json;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Services;

public sealed class CleanupFilterTests : IDisposable
{
    private readonly FakeEnvironment _environment = new();
    private readonly CleanupFilter _filter;
    private readonly string _root;

    public CleanupFilterTests()
    {
        _root = _environment.CreateTempDirectory();
        _filter = JbRunners.Filter(_environment.CreateTempDirectory());
    }

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public void Decide_FileInAProjectWithAReference_FiltersToTheProjectAndItsReference()
    {
        // Arrange
        string solution = SolutionFiles.PlantThreeProjects(_root);
        ResolvedConfig config = Configs.Bare(solution, _environment.CreateTempDirectory());

        // Act
        CleanupScope scope = _filter.Decide(config, ["A/Foo.cs"]);

        // Assert — the filter names the solution by its absolute path, so it can live anywhere, and each project
        // as the solution spells it.
        SolutionFilterPlan filter = scope.Filter.ShouldNotBeNull(scope.SolutionWideBecause);
        filter.Path.ShouldBe(_filter.PathFor(solution, config.CacheHome));
        filter.ProjectCount.ShouldBe(2);
        filter.SolutionProjectCount.ShouldBe(3);

        using JsonDocument content = JsonDocument.Parse(filter.Content);
        JsonElement declared = content.RootElement.GetProperty("solution");
        declared.GetProperty("path").GetString().ShouldBe(solution);
        declared.GetProperty("projects").EnumerateArray().Select(project => project.GetString()).ShouldBe(["A/A.csproj", "B/B.csproj"]);
    }

    [Theory]
    [InlineData("switched off", "RESHARPER_MCP_CLEANUP_FILTER is off")]
    [InlineData("already a filter", "the solution is already a solution filter")]
    [InlineData("wildcard", "\"A/*.cs\" is a wildcard, which only jb can expand")]
    [InlineData("no owner", "\"Loose.cs\": no project of the solution is in a directory above it")]
    [InlineData("two owners", "holds 2 of the solution's projects, so which one owns it is ambiguous")]
    [InlineData("unreadable solution", "the solution file could not be read")]
    [InlineData("unreadable reference", "references a project as \"$(Root)/B/B.csproj\"")]
    [InlineData("every project", "the files' projects and their references are all 2 of the solution's projects")]
    public void Decide_NoFilterCouldNarrowIt_RunsSolutionWideSayingWhy(string shape, string reason)
    {
        // Arrange
        string solution = SolutionFiles.PlantThreeProjects(_root);
        IReadOnlyList<string> files = ["A/Foo.cs"];
        CleanupFilter filter = _filter;

        switch (shape)
        {
            case "switched off":
                filter = JbRunners.Filter(_environment.CreateTempDirectory(), false);
                break;
            case "already a filter":
                solution = SolutionFiles.Plant(_root, "App.slnf", "{}");
                break;
            case "wildcard":
                files = ["A/Foo.cs", "A/*.cs"];
                break;
            case "no owner":
                SolutionFiles.Plant(_root, "Loose.cs");
                files = ["Loose.cs"];
                break;
            case "two owners":
                SolutionFiles.PlantProject(_root, "A/Twin.csproj");
                solution = SolutionFiles.PlantSlnx(_root, "App.slnx", "A/A.csproj", "A/Twin.csproj", "B/B.csproj");
                break;
            case "unreadable solution":
                solution = SolutionFiles.Plant(_root, "App.slnx", "<Solution>");
                break;
            case "unreadable reference":
                SolutionFiles.PlantProject(_root, "A/A.csproj", "$(Root)/B/B.csproj");
                break;
            case "every project":
                solution = SolutionFiles.PlantSlnx(_root, "App.slnx", "A/A.csproj", "B/B.csproj");
                break;
        }

        // Act
        CleanupScope scope = filter.Decide(Configs.Bare(solution, _environment.CreateTempDirectory()), files);

        // Assert
        scope.Filter.ShouldBeNull();
        scope.SolutionWideBecause.ShouldNotBeNull().ShouldContain(reason);
    }

    [Fact]
    public void PathFor_OneSolutionSpeltInTwoCasesOnWindows_IsOneFilter()
    {
        // Arrange — a filter is named with the key of the run lock it is written under.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the run lock folds case on Windows only");
        string cacheHome = _environment.CreateTempDirectory();

        // Act
        string upper = _filter.PathFor(@"C:\Repo\App.slnx", cacheHome);
        string lower = _filter.PathFor(@"c:\repo\app.slnx", cacheHome);

        // Assert — only the display name keeps the caller's casing.
        lower.ShouldBe(upper, StringCompareShould.IgnoreCase);
    }

    [Fact]
    public void PathFor_TwoCheckoutsOfOneSolution_AreTwoFilters()
    {
        // Arrange
        string cacheHome = _environment.CreateTempDirectory();

        // Act
        string first = _filter.PathFor(Path.Combine(_root, "one", "App.slnx"), cacheHome);
        string second = _filter.PathFor(Path.Combine(_root, "two", "App.slnx"), cacheHome);

        // Assert
        Path.GetFileName(first).ShouldNotBe(Path.GetFileName(second));
    }

    [Fact]
    public void PathFor_OneCheckoutUnderTwoCacheHomes_IsTwoFilters()
    {
        // Arrange — two sessions on one checkout with different cache homes queue on different locks, so one
        // file between them could be rewritten by either while the other's jb reads it.
        string solution = Path.Combine(_root, "App.slnx");

        // Act
        string first = _filter.PathFor(solution, _environment.CreateTempDirectory());
        string second = _filter.PathFor(solution, _environment.CreateTempDirectory());

        // Assert
        Path.GetFileName(first).ShouldNotBe(Path.GetFileName(second));
    }
}