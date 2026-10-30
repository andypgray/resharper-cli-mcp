using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Services;

public sealed class CleanupServiceArgumentTests
{
    private const string SolutionPath = "/sln/App.sln";
    private const string CacheHome = "/cache";

    [Fact]
    public void BuildArguments_MinimalConfig_ProducesExactFixedOrder()
    {
        // Act
        List<string> arguments = CleanupService.BuildArguments(
            Configs.Bare(SolutionPath, CacheHome), ["src/A.cs"], CleanupService.DefaultProfile);

        // Assert
        arguments.ShouldBe(
        [
            "cleanupcode",
            "/sln/App.sln",
            "--profile=Built-in: Full Cleanup",
            "--no-build",
            "--include=src/A.cs",
            "--caches-home=/cache"
        ]);
    }

    [Fact]
    public void BuildArguments_AllOptionsPresent_AppendsInPinnedOrder()
    {
        // Act — the settings file is one jb cannot discover, which is the only shape that earns --settings.
        List<string> arguments = CleanupService.BuildArguments(
            Configs.With(SolutionPath, CacheHome, "/team/Shared.DotSettings", true, "Cfg.Ext", "cfg-source"),
            ["A.cs", "B.cs"],
            "Custom: No Reordering");

        // Assert
        arguments.ShouldBe(
        [
            "cleanupcode",
            "/sln/App.sln",
            "--profile=Custom: No Reordering",
            "--no-build",
            "--include=A.cs;B.cs",
            "--caches-home=/cache",
            "--settings=/team/Shared.DotSettings",
            "-x=Cfg.Ext",
            "--source=cfg-source"
        ]);
    }

    [Fact]
    public void BuildArguments_ThroughAFilter_NamesTheFilterWhereTheSolutionWouldBe()
    {
        // Act
        List<string> arguments = CleanupService.BuildArguments(
            Configs.Bare(SolutionPath, CacheHome), ["**/sln/src/A.cs"], CleanupService.DefaultProfile, "/tmp/App.cleanup-0123.slnf");

        // Assert
        arguments.ShouldBe(
        [
            "cleanupcode",
            "/tmp/App.cleanup-0123.slnf",
            "--profile=Built-in: Full Cleanup",
            "--no-build",
            "--include=**/sln/src/A.cs",
            "--caches-home=/cache"
        ]);
    }

    [Fact]
    public void IncludePatterns_ThroughAFilter_AnchorsOnTheWholePathWhereASolutionWideRunStaysRelative()
    {
        // Arrange — rooted at whatever this platform's root is, so the anchored spelling is the same everywhere.
        string solutionDirectory = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "repo");

        // Act
        List<string> filtered = CleanupService.IncludePatterns(["src/A.cs"], solutionDirectory, true);
        List<string> solutionWide = CleanupService.IncludePatterns(["src/A.cs"], solutionDirectory, false);

        // Assert
        filtered.ShouldBe(["**/repo/src/A.cs"]);
        solutionWide.ShouldBe(["src/A.cs"]);
    }
}