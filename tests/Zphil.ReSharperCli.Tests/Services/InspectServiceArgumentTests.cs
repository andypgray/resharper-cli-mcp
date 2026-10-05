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
    private const string CacheHome = "/cache";
    private const string OutputFile = "/tmp/out/results.json";
    private const string SolutionPath = "/sln/App.sln";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void BuildArguments_MinimalConfig_ProducesExactFixedOrder()
    {
        // Act
        List<string> arguments = InspectService.BuildArguments(Configs.Bare(SolutionPath, CacheHome), OutputFile, null, InspectSeverity.Warning);

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
            Configs.With(SolutionPath, CacheHome, "/team/Shared.DotSettings", true, "Cfg.Ext", "cfg-source"),
            OutputFile,
            ["src/A.cs", "src/B.cs"],
            InspectSeverity.Error);

        // Assert — --include precedes the shared config tail, which inspect and cleanup append through one
        // helper (jb is flag-order-insensitive, so the order is this server's choice).
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
    public void BuildArguments_EmptyFiles_OmitsIncludeFlag()
    {
        // Act
        List<string> arguments = InspectService.BuildArguments(Configs.Bare(SolutionPath, CacheHome), OutputFile, [], InspectSeverity.Warning);

        // Assert
        arguments.Any(a => a.StartsWith("--include", StringComparison.Ordinal)).ShouldBeFalse();
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
        ResolvedConfig config = Configs.With(
            SolutionPath, environment.CreateTempDirectory(), "/team/Shared.DotSettings", true, "Cfg.Ext", "cfg-source");
        var processRunner = Substitute.For<IProcessRunner>();
        IReadOnlyList<string>? captured = null;
        processRunner
            .AnyRun()
            .Returns(call =>
            {
                captured = call.Arguments();
                return JbStubs.Success;
            });
        InspectService service = new(JbRunners.Create(processRunner));

        // Act
        await service.WarmCacheAsync(config, Ct);

        // Assert — element for element, modulo the throwaway output path, so the warm-up cannot drift into
        // opening a different cache generation from the one a real call opens.
        captured.ShouldNotBeNull();
        string outputFile = JbStubs.OutputPathOf(captured).ShouldNotBeNull();
        captured.ShouldBe(InspectService.BuildArguments(config, outputFile, null, InspectService.WarmUpSeverity));
        captured.Any(argument => argument.StartsWith("--include", StringComparison.Ordinal)).ShouldBeFalse();
    }
}