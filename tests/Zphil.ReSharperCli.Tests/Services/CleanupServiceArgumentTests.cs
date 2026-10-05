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
}