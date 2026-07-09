using NSubstitute;
using NSubstitute.Core;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Tests.Tools;

/// <summary>
///     End-to-end through the tool methods over the two faked seams: a tool call probes jb, resolves the
///     solution from the working directory, runs jb, and shapes the result. jb is faked; the config +
///     service graph is real.
/// </summary>
public sealed class ToolPipelineTests
{
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InspectAsync_SolutionInWorkingDirectory_ReturnsFormattedIssues()
    {
        // Arrange
        using FakeEnvironment environment = new();
        PlantSolution(environment, "App.sln");
        string sarif = Fixtures.ReadSarif("inspect-sample.json");
        StubJb(sarif);
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        string result = await tools.InspectAsync(cancellationToken: Ct);

        // Assert
        result.ShouldStartWith("Found 3 issue(s)");
    }

    // The enum is used as a body literal, not a public method parameter: the internal InspectSeverity
    // cannot appear in a public [Theory] signature (CS0051), so these are two facts. Case-insensitive
    // string INPUT (e.g. "warning") is now coerced/validated at the binding layer — see the converter
    // and coercion tests. These pin the enum → jb CLI-token mapping (and that a non-default value drives it).
    [Fact]
    public async Task InspectAsync_WarningSeverity_MapsToWarningCliToken()
    {
        // Arrange
        using FakeEnvironment environment = new();
        PlantSolution(environment, "App.sln");
        List<string>? inspectArguments = null;
        StubJb(
            Fixtures.ReadSarif("inspect-sample.json"),
            args => inspectArguments = [.. args]);
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        await tools.InspectAsync(severity: InspectSeverity.Warning, cancellationToken: Ct);

        // Assert
        inspectArguments.ShouldNotBeNull();
        inspectArguments.ShouldContain("--severity=WARNING");
    }

    [Fact]
    public async Task InspectAsync_ErrorSeverity_MapsToErrorCliToken()
    {
        // Arrange
        using FakeEnvironment environment = new();
        PlantSolution(environment, "App.sln");
        List<string>? inspectArguments = null;
        StubJb(
            Fixtures.ReadSarif("inspect-sample.json"),
            args => inspectArguments = [.. args]);
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act — a non-default value proves the parameter, not the default, drives the CLI token.
        await tools.InspectAsync(severity: InspectSeverity.Error, cancellationToken: Ct);

        // Assert
        inspectArguments.ShouldNotBeNull();
        inspectArguments.ShouldContain("--severity=ERROR");
    }

    [Fact]
    public async Task CleanupAsync_ValidFiles_ReturnsSummaryWithProfileAndFiles()
    {
        // Arrange
        using FakeEnvironment environment = new();
        PlantSolution(environment, "App.sln");
        PlantFile(environment, "src/A.cs");
        PlantFile(environment, "src/B.cs");
        StubJb();
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        string result = await tools.CleanupAsync(["src/A.cs", "src/B.cs"], cancellationToken: Ct);

        // Assert
        result.ShouldBe(
            "Cleanup completed for 2 file(s) with profile \"Built-in: Full Cleanup\":\n"
            + "  - src/A.cs\n"
            + "  - src/B.cs");
    }

    [Fact]
    public async Task InspectAsync_NoSolutionInWorkingDirectory_ThrowsUserErrorMentioningJbSolutionPath()
    {
        // Arrange — the working directory is an empty temp dir, so discovery finds no solution.
        using FakeEnvironment environment = new();
        StubJb();
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => tools.InspectAsync(cancellationToken: Ct));

        // Assert
        exception.Message.ShouldContain("JB_SOLUTION_PATH");
    }

    private static void PlantSolution(FakeEnvironment environment, string fileName)
    {
        File.WriteAllText(Path.Combine(environment.CurrentDirectory, fileName), string.Empty);
    }

    private static void PlantFile(FakeEnvironment environment, string relativePath)
    {
        string fullPath = Path.Combine(environment.CurrentDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, string.Empty);
    }

    /// <summary>
    ///     Routes the single process-runner substitute by jb sub-command: the version probe, an
    ///     inspectcode run (which writes <paramref name="inspectSarif" /> to its <c>-o=</c> path), or a
    ///     cleanupcode run. All succeed with exit code 0.
    /// </summary>
    private void StubJb(string inspectSarif = "", Action<IReadOnlyList<string>>? onInspect = null)
    {
        _processRunner
            .RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Route);

        return;

        ProcessResult Route(CallInfo callInfo)
        {
            var arguments = callInfo.ArgAt<IReadOnlyList<string>>(1);

            if (arguments.Contains("--version")) return new ProcessResult(0, "Version: 2026.1.2", string.Empty);

            if (arguments.Count > 0 && arguments[0] == "inspectcode")
            {
                onInspect?.Invoke(arguments);
                File.WriteAllText(OutputPathFrom(arguments), inspectSarif);
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        }
    }

    private static string OutputPathFrom(IReadOnlyList<string> arguments)
    {
        string arg = arguments.First(a => a.StartsWith("-o=", StringComparison.Ordinal));
        return arg["-o=".Length..];
    }
}