using System.Reflection;
using NSubstitute;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
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
        InspectService.WarmUpSeverity.ShouldBe(toolDefault.ToString().ToUpperInvariant());
    }

    [Fact]
    public async Task WarmCacheAsync_BuildsWhatADefaultSolutionWideInspectBuilds()
    {
        // Arrange
        using FakeEnvironment environment = new();
        ResolvedConfig config = new(
            "/sln/App.sln", "/sln/App.sln.DotSettings", null, environment.CreateTempDirectory(), "Cfg.Ext", "cfg-source", "jb");
        var processRunner = Substitute.For<IProcessRunner>();
        IReadOnlyList<string>? captured = null;
        processRunner
            .RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<IReadOnlyList<string>>();
                return new ProcessResult(0, string.Empty, string.Empty);
            });
        InspectService service = new(new JbRunner(processRunner, new JbRunLock()));

        // Act
        await service.WarmCacheAsync(config, Ct);

        // Assert — element for element, modulo the throwaway output path, so the warm-up cannot drift into
        // opening a different cache generation from the one a real call opens.
        captured.ShouldNotBeNull();
        string outputFile = captured.Single(argument => argument.StartsWith("-o=", StringComparison.Ordinal))["-o=".Length..];
        captured.ShouldBe(InspectService.BuildArguments(config, outputFile, null, InspectService.WarmUpSeverity));
        captured.Any(argument => argument.StartsWith("--include", StringComparison.Ordinal)).ShouldBeFalse();
    }

    private static ResolvedConfig Config(string? settings = null, string? extensions = null, string? extensionSource = null)
    {
        return new ResolvedConfig(
            "/sln/App.sln",
            settings,
            null,
            "/cache",
            extensions,
            extensionSource,
            "jb");
    }
}