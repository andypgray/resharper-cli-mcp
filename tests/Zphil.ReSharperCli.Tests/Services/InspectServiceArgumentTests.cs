using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Tests.Services;

public sealed class InspectServiceArgumentTests
{
    private const string OutputFile = "/tmp/out/results.json";

    [Fact]
    public void BuildArguments_MinimalConfig_ProducesExactFixedOrder()
    {
        // Act
        var arguments = InspectService.BuildArguments(Config(), OutputFile, null, "WARNING");

        // Assert
        arguments.ShouldBe(
        [
            "inspectcode",
            "/sln/App.sln",
            "-o=/tmp/out/results.json",
            "--severity=WARNING",
            "--swea",
            "--no-build",
            "--absolute-paths",
            "--caches-home=/cache"
        ]);
    }

    [Fact]
    public void BuildArguments_AllOptionsPresent_AppendsInPinnedOrder()
    {
        // Act
        var arguments = InspectService.BuildArguments(
            Config("/sln/App.sln.DotSettings", "Cfg.Ext", "cfg-source"),
            OutputFile,
            ["src/A.cs", "src/B.cs"],
            "ERROR");

        // Assert
        arguments.ShouldBe(
        [
            "inspectcode",
            "/sln/App.sln",
            "-o=/tmp/out/results.json",
            "--severity=ERROR",
            "--swea",
            "--no-build",
            "--absolute-paths",
            "--caches-home=/cache",
            "--settings=/sln/App.sln.DotSettings",
            "--include=src/A.cs;src/B.cs",
            "-x=Cfg.Ext",
            "--source=cfg-source"
        ]);
    }

    [Fact]
    public void BuildArguments_MultipleFiles_JoinsIncludeWithSemicolons()
    {
        // Act
        var arguments = InspectService.BuildArguments(
            Config(), OutputFile, ["A.cs", "B.cs", "C.cs"], "WARNING");

        // Assert
        arguments.ShouldContain("--include=A.cs;B.cs;C.cs");
    }

    [Fact]
    public void BuildArguments_EmptyFiles_OmitsIncludeFlag()
    {
        // Act
        var arguments = InspectService.BuildArguments(Config(), OutputFile, [], "WARNING");

        // Assert
        arguments.Any(a => a.StartsWith("--include", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public void BuildArguments_NullSettings_OmitsSettingsFlag()
    {
        // Act
        var arguments = InspectService.BuildArguments(Config(), OutputFile, null, "WARNING");

        // Assert
        arguments.Any(a => a.StartsWith("--settings", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public void BuildArguments_ConfigExtensions_AppendsExtensionFlags()
    {
        // Act
        var arguments = InspectService.BuildArguments(
            Config(extensions: "Cfg.Ext", extensionSource: "cfg-source"), OutputFile, null, "WARNING");

        // Assert
        arguments.ShouldContain("-x=Cfg.Ext");
        arguments.ShouldContain("--source=cfg-source");
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