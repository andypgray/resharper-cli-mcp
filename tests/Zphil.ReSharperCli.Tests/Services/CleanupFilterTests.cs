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
    [InlineData("linked", "\"A/Foo.cs\": C/C.csproj may compile it too, through Compile Include \"../A/Foo.cs\"")]
    [InlineData("linked by glob", "\"A/Foo.cs\": C/C.csproj may compile it too, through Compile Include \"../A/**/*.cs\"")]
    [InlineData("conditional prefix", "\"A/Foo.cs\": C/C.csproj may compile it too, through Compile Include \"../$(Prefix)A/Foo.cs\"")]
    [InlineData("compile remove", "\"A/Foo.cs\": A/A.csproj may not compile it as its directory implies, through Compile Remove \"Foo.cs\"")]
    [InlineData("compile update", "\"A/Foo.cs\": A/A.csproj may not compile it as its directory implies, through Compile Update \"Foo.cs\"")]
    [InlineData("default items off", "\"A/Foo.cs\": A/A.csproj sets EnableDefaultCompileItems to \"false\", so its directory may not say what it compiles")]
    [InlineData("non-SDK owner", "\"A/Foo.cs\": A/A.csproj is not an SDK project, so only its own item list says what it compiles")]
    [InlineData("nested under another project", "\"A/Foo.cs\": Root.csproj is in a directory above A/A.csproj, so its default items may compile it too")]
    [InlineData("unreadable other project", "C.csproj could not be read (")]
    [InlineData("unevaluable include", "C.csproj declares Compile Include \"@(Shared)\", which needs MSBuild to evaluate")]
    [InlineData("escaped link", "\"A/Foo.cs\": C/C.csproj may compile it too, through Compile Include \"../A/Foo%2Ecs\"")]
    [InlineData("hooked targets file", "\"A/Foo.cs\": C/C.csproj may compile it too, through Compile Include \"../A/Foo.cs\"")]
    [InlineData("default items cleared", "\"A/Foo.cs\": A/A.csproj sets EnableDefaultCompileItems to \"\", so its directory may not say what it compiles")]
    [InlineData("package import", "C.csproj imports \"$(NuGetPackageRoot)x/build/x.targets\" from the package folder, which is not read")]
    [InlineData("razor file", "\"A/Counter.razor\": only a .cs file is compiled through the Compile items of A/A.csproj, the only items read to prove its owner")]
    [InlineData("vb file under a C# project", "\"A/Foo.vb\": only a .cs file is compiled through the Compile items of A/A.csproj, the only items read to prove its owner")]
    [InlineData("F# owner", "\"A/Foo.cs\": A/A.fsproj is not a C# or Visual Basic project, whose default Compile items are the only ones read")]
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
            case "linked":
                SolutionFiles.PlantSdkProject(_root, "C/C.csproj", """<ItemGroup><Compile Include="../A/Foo.cs" Link="Foo.cs" /></ItemGroup>""");
                break;
            case "linked by glob":
                SolutionFiles.PlantSdkProject(_root, "C/C.csproj", """<ItemGroup><Compile Include="../A/**/*.cs" /></ItemGroup>""");
                break;
            case "conditional prefix":
                // Set only under a condition, so unset is a spelling too, and that one names A's file.
                SolutionFiles.PlantSdkProject(
                    _root,
                    "C/C.csproj",
                    """
                    <PropertyGroup Condition="'$(Mode)' == 'other'"><Prefix>Other</Prefix></PropertyGroup>
                    <ItemGroup><Compile Include="../$(Prefix)A/Foo.cs" /></ItemGroup>
                    """);
                break;
            case "compile remove":
                PlantAWith("""<Compile Remove="Foo.cs" />""");
                break;
            case "compile update":
                PlantAWith("""<Compile Update="Foo.cs" DependentUpon="Bar.cs" />""");
                break;
            case "default items off":
                SolutionFiles.PlantSdkProject(
                    _root,
                    "A/A.csproj",
                    """
                    <PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
                    <ItemGroup><ProjectReference Include="../B/B.csproj" /><Compile Include="Foo.cs" /></ItemGroup>
                    """);
                break;
            case "non-SDK owner":
                SolutionFiles.Plant(
                    _root,
                    "A/A.csproj",
                    """
                    <Project ToolsVersion="15.0">
                      <ItemGroup><ProjectReference Include="../B/B.csproj" /><Compile Include="Foo.cs" /></ItemGroup>
                      <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
                    </Project>
                    """);
                break;
            case "nested under another project":
                SolutionFiles.PlantProject(_root, "Root.csproj");
                solution = SolutionFiles.PlantSlnx(_root, "App.slnx", "A/A.csproj", "B/B.csproj", "C/C.csproj", "Root.csproj");
                break;
            case "unreadable other project":
                SolutionFiles.Plant(_root, "C/C.csproj", "<Project>");
                break;
            case "unevaluable include":
                SolutionFiles.PlantSdkProject(_root, "C/C.csproj", """<ItemGroup><Compile Include="@(Shared)" /></ItemGroup>""");
                break;
            case "escaped link":
                SolutionFiles.PlantSdkProject(_root, "C/C.csproj", """<ItemGroup><Compile Include="../A/Foo%2Ecs" /></ItemGroup>""");
                break;
            case "hooked targets file":
                SolutionFiles.PlantSdkProject(
                    _root,
                    "C/C.csproj",
                    """<PropertyGroup><DirectoryBuildTargetsPath>$(MSBuildProjectDirectory)/../build/C.targets</DirectoryBuildTargetsPath></PropertyGroup>""");
                SolutionFiles.Plant(_root, "build/C.targets", """<Project><ItemGroup><Compile Include="../A/Foo.cs" /></ItemGroup></Project>""");
                break;
            case "default items cleared":
                SolutionFiles.PlantSdkProject(
                    _root,
                    "A/A.csproj",
                    """
                    <PropertyGroup><EnableDefaultCompileItems></EnableDefaultCompileItems></PropertyGroup>
                    <ItemGroup><ProjectReference Include="../B/B.csproj" /></ItemGroup>
                    """);
                break;
            case "package import":
                SolutionFiles.PlantSdkProject(_root, "C/C.csproj", """<Import Project="$(NuGetPackageRoot)x/build/x.targets" />""");
                break;
            case "razor file":
                SolutionFiles.Plant(_root, "A/Counter.razor");
                files = ["A/Counter.razor"];
                break;
            case "vb file under a C# project":
                SolutionFiles.Plant(_root, "A/Foo.vb");
                files = ["A/Foo.vb"];
                break;
            case "F# owner":
                SolutionFiles.PlantProject(_root, "A/A.fsproj", "../B/B.csproj");
                solution = SolutionFiles.PlantSlnx(_root, "App.slnx", "A/A.fsproj", "B/B.csproj", "C/C.csproj");
                break;
        }

        // Act
        CleanupScope scope = filter.Decide(Configs.Bare(solution, _environment.CreateTempDirectory()), files);

        // Assert
        scope.Filter.ShouldBeNull();
        scope.SolutionWideBecause.ShouldNotBeNull().ShouldContain(reason);
    }

    [Theory]
    [InlineData("A/A.csproj", """<ItemGroup><ProjectReference Include="../B/B.csproj" /><Compile Remove="Other/**" /></ItemGroup>""")]
    [InlineData("C/C.csproj", """<ItemGroup><Compile Include="../A/Bar.cs" Link="Bar.cs" /></ItemGroup>""")]
    public void Decide_CompileItemsThatCannotNameTheFile_StillFilter(string project, string body)
    {
        // Arrange — A's removal and C's link each name a file beside Foo.cs, never Foo.cs itself.
        string solution = SolutionFiles.PlantThreeProjects(_root);
        SolutionFiles.PlantSdkProject(_root, project, body);

        // Act
        CleanupScope scope = _filter.Decide(Configs.Bare(solution, _environment.CreateTempDirectory()), ["A/Foo.cs"]);

        // Assert
        scope.Filter.ShouldNotBeNull(scope.SolutionWideBecause).ProjectCount.ShouldBe(2);
    }

    [Theory]
    [InlineData("A/A.csproj", "A/Bar.CS")]
    [InlineData("A/A.vbproj", "A/Foo.vb")]
    public void Decide_ASourceFileInItsProjectsLanguage_Filters(string project, string file)
    {
        // Arrange — project A in the row's language, still referencing B.
        SolutionFiles.PlantThreeProjects(_root);
        SolutionFiles.PlantProject(_root, project, "../B/B.csproj");
        string solution = SolutionFiles.PlantSlnx(_root, "App.slnx", project, "B/B.csproj", "C/C.csproj");
        SolutionFiles.Plant(_root, file);

        // Act
        CleanupScope scope = _filter.Decide(Configs.Bare(solution, _environment.CreateTempDirectory()), [file]);

        // Assert
        scope.Filter.ShouldNotBeNull(scope.SolutionWideBecause).ProjectCount.ShouldBe(2);
    }

    /// <summary>Replaces project A with one that keeps its reference to B and adds <paramref name="items" />.</summary>
    private void PlantAWith(string items)
    {
        SolutionFiles.PlantSdkProject(_root, "A/A.csproj", $"""<ItemGroup><ProjectReference Include="../B/B.csproj" />{items}</ItemGroup>""");
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