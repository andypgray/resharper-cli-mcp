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
        List<string> arguments = CleanupService.BuildArguments(
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
        // Act — the settings file is one jb cannot discover, which is the only shape that earns --settings.
        List<string> arguments = CleanupService.BuildArguments(
            Config("/team/Shared.DotSettings", true, "Cfg.Ext", "cfg-source"),
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
    public void BuildArguments_NullSettings_OmitsSettingsFlag()
    {
        // Act
        List<string> arguments = CleanupService.BuildArguments(Config(), ["src/A.cs"], CleanupService.DefaultProfile);

        // Assert
        arguments.Any(a => a.StartsWith("--settings", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public void BuildArguments_SettingsFileJbDiscoversItself_OmitsSettingsFlag()
    {
        // Act — resolved, but a file jb mounts on its own (the adjacent .DotSettings). Passing it as
        // --settings would re-mount it as a Custom layer above the project layers, and cleanup rewrites
        // files, so the style a {project}.csproj.DotSettings protects would be normalized away.
        List<string> arguments = CleanupService.BuildArguments(
            Config("/sln/App.sln.DotSettings"), ["src/A.cs"], CleanupService.DefaultProfile);

        // Assert
        arguments.Any(a => a.StartsWith("--settings", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public void BuildArguments_DefaultProfileConstant_IsBuiltInFullCleanup()
    {
        // Assert
        CleanupService.DefaultProfile.ShouldBe("Built-in: Full Cleanup");
    }

    private static ResolvedConfig Config(
        string? settings = null,
        bool settingsIsCustomLayer = false,
        string? extensions = null,
        string? extensionSource = null)
    {
        return new ResolvedConfig(
            "/sln/App.sln",
            settings,
            settingsIsCustomLayer,
            null,
            "/cache",
            extensions,
            extensionSource,
            "jb",
            ConfigWarnings.None);
    }
}