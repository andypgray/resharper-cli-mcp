using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Tests.Services;

public sealed class CleanupServiceArgumentTests
{
    [Fact]
    public void BuildArguments_MinimalConfig_ProducesExactFixedOrder()
    {
        // Act
        var arguments = CleanupService.BuildArguments(
            Config(), ["src/A.cs"], CleanupService.DefaultProfile);

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
        // Act
        var arguments = CleanupService.BuildArguments(
            Config("/sln/App.sln.DotSettings", "Cfg.Ext", "cfg-source"),
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
            "--settings=/sln/App.sln.DotSettings",
            "-x=Cfg.Ext",
            "--source=cfg-source"
        ]);
    }

    [Fact]
    public void BuildArguments_DefaultProfileConstant_IsBuiltInFullCleanup()
    {
        // Assert
        CleanupService.DefaultProfile.ShouldBe("Built-in: Full Cleanup");
    }

    private static ResolvedConfig Config(string? settings = null, string? extensions = null, string? extensionSource = null)
    {
        return new ResolvedConfig(
            "/sln/App.sln",
            settings,
            "/cache",
            extensions,
            extensionSource,
            "jb");
    }
}