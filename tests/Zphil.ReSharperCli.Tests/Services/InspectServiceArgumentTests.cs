using System.Reflection;
using NSubstitute;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Tests.Services;

public sealed class InspectServiceArgumentTests
{
    private const string OutputFile = "/tmp/out/results.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void BuildArguments_MinimalConfig_ProducesExactFixedOrder()
    {
        // Act
        List<string> arguments = InspectService.BuildArguments(Config(), OutputFile, null, InspectSeverity.Warning);

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
        // Act — the settings file is one jb cannot discover, which is the only shape that earns --settings.
        List<string> arguments = InspectService.BuildArguments(
            Config("/team/Shared.DotSettings", true, "Cfg.Ext", "cfg-source"),
            OutputFile,
            ["src/A.cs", "src/B.cs"],
            InspectSeverity.Error);

        // Assert — --include precedes the shared config tail (jb is flag-order-insensitive; the pin moved
        // when the tail was extracted so inspect and cleanup append configuration through one helper).
        arguments.ShouldBe(
        [
            "inspectcode",
            "/sln/App.sln",
            "-o=/tmp/out/results.json",
            "--severity=ERROR",
            "--swea",
            "--no-build",
            "--absolute-paths",
            "--include=src/A.cs;src/B.cs",
            "--caches-home=/cache",
            "--settings=/team/Shared.DotSettings",
            "-x=Cfg.Ext",
            "--source=cfg-source"
        ]);
    }

    [Fact]
    public void BuildArguments_MultipleFiles_JoinsIncludeWithSemicolons()
    {
        // Act
        List<string> arguments = InspectService.BuildArguments(
            Config(), OutputFile, ["A.cs", "B.cs", "C.cs"], InspectSeverity.Warning);

        // Assert
        arguments.ShouldContain("--include=A.cs;B.cs;C.cs");
    }

    [Fact]
    public void BuildArguments_EmptyFiles_OmitsIncludeFlag()
    {
        // Act
        List<string> arguments = InspectService.BuildArguments(Config(), OutputFile, [], InspectSeverity.Warning);

        // Assert
        arguments.Any(a => a.StartsWith("--include", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public void BuildArguments_NullSettings_OmitsSettingsFlag()
    {
        // Act
        List<string> arguments = InspectService.BuildArguments(Config(), OutputFile, null, InspectSeverity.Warning);

        // Assert
        arguments.Any(a => a.StartsWith("--settings", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public void BuildArguments_SettingsFileJbDiscoversItself_OmitsSettingsFlag()
    {
        // Act — resolved, but a file jb mounts on its own (the adjacent .DotSettings). Passing it as
        // --settings would re-mount it as a Custom layer above the project layers, silently demoting
        // every {project}.csproj.DotSettings in the solution.
        List<string> arguments = InspectService.BuildArguments(
            Config("/sln/App.sln.DotSettings"), OutputFile, null, InspectSeverity.Warning);

        // Assert
        arguments.Any(a => a.StartsWith("--settings", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public void BuildArguments_ConfigExtensions_AppendsExtensionFlags()
    {
        // Act
        List<string> arguments = InspectService.BuildArguments(
            Config(extensions: "Cfg.Ext", extensionSource: "cfg-source"), OutputFile, null, InspectSeverity.Warning);

        // Assert
        arguments.ShouldContain("-x=Cfg.Ext");
        arguments.ShouldContain("--source=cfg-source");
    }

    [Fact]
    public void WarmUpSeverity_IsTheResharperInspectDefault()
    {
        // Arrange — read the tool's own declared default rather than restating it, so lowering or raising
        // that default fails here instead of quietly leaving the pre-warm on the old one.
        ParameterInfo severityParameter = typeof(ResharperTools)
            .GetMethod(nameof(ResharperTools.InspectAsync))!
            .GetParameters()
            .Single(parameter => parameter.Name == "severity");
        var toolDefault = (InspectSeverity)severityParameter.DefaultValue!;

        // Assert — this is the pin that keeps a pre-warm warming the generation a real call opens. If the
        // two argument lists ever diverge, the feature silently warms a cache nothing reads and says nothing.
        InspectService.WarmUpSeverity.ShouldBe(toolDefault);
    }

    [Fact]
    public async Task WarmCacheAsync_BuildsWhatADefaultSolutionWideInspectBuilds()
    {
        // Arrange
        using FakeEnvironment environment = new();
        ResolvedConfig config = new(
            "/sln/App.sln", "/team/Shared.DotSettings", true, null, environment.CreateTempDirectory(), "Cfg.Ext", "cfg-source", "jb",
            ConfigWarnings.None);
        var processRunner = Substitute.For<IProcessRunner>();
        IReadOnlyList<string>? captured = null;
        processRunner
            .RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<IReadOnlyList<string>>();
                return new ProcessResult(0, string.Empty, string.Empty);
            });
        InspectService service = new(JbRunners.Create(processRunner));

        // Act
        await service.WarmCacheAsync(config, Ct);

        // Assert — element for element, modulo the throwaway output path, so the warm-up cannot drift into
        // opening a different cache generation from the one a real call opens.
        captured.ShouldNotBeNull();
        string outputFile = captured.Single(argument => argument.StartsWith("-o=", StringComparison.Ordinal))["-o=".Length..];
        captured.ShouldBe(InspectService.BuildArguments(config, outputFile, null, InspectService.WarmUpSeverity));
        captured.Any(argument => argument.StartsWith("--include", StringComparison.Ordinal)).ShouldBeFalse();
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