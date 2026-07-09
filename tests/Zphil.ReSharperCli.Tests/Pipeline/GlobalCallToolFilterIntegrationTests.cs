using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Pipeline;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Pipeline;

/// <summary>
///     Drives a real MCP client against the server over in-memory pipes to lock down
///     <see cref="GlobalCallToolFilter" />'s three branches — silent user-error, logged unexpected-error,
///     truncated success — end to end, plus two regression pins (cleanup's required <c>files</c>
///     schema and the negotiated server identity). It is the automated stand-in for a manual stdio
///     smoke test.
/// </summary>
public sealed class GlobalCallToolFilterIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CallTool_UserError_ReturnsErrorResultAndLogsNothing()
    {
        // Arrange — cleanup with an empty file list throws UserErrorException before touching jb.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Act
        CallToolResult result = await harness.Client.CallToolAsync(
            "resharper_cleanup",
            new Dictionary<string, object?> { ["files"] = Array.Empty<string>() },
            cancellationToken: Ct);

        // Assert — surfaced as an error result with the exact message, and the filter stayed silent.
        result.IsError.ShouldBe(true);
        TextOf(result).ShouldBe("At least one file must be specified.");
        harness.Logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task CallTool_UnexpectedError_LogsExactlyOneWarningNamingTheTool()
    {
        // Arrange — jb is found (probe succeeds) and a solution is present, so the tool reaches the
        // inspectcode run; that run throws a non-UserError exception, which must escape the tool method.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        PlantSolution(harness.Environment, "App.sln");
        RouteJb(harness.ProcessRunner, _ => throw new IOException("SARIF output target vanished"));

        // Act
        CallToolResult result = await harness.Client.CallToolAsync("resharper_inspect", cancellationToken: Ct);

        // Assert — surfaced as an error, and logged exactly once as a warning that names the tool.
        result.IsError.ShouldBe(true);
        LogEntry warning = harness.Logs.Warnings.ShouldHaveSingleItem();
        warning.Message.ShouldContain("resharper_inspect");
        warning.Category.ShouldBe(typeof(GlobalCallToolFilter).FullName);
        warning.Exception.ShouldBeOfType<IOException>();
    }

    [Fact]
    public async Task CallTool_SuccessOverBudget_TruncatesWithInspectHintAndLogsNothing()
    {
        // Arrange — a 40-token budget (100-char cap) against the 3-issue fixture forces truncation.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        harness.Environment.SetVariable("MAX_MCP_OUTPUT_TOKENS", "40");
        PlantSolution(harness.Environment, "App.sln");
        string sarif = Fixtures.ReadSarif("inspect-sample.json");
        RouteJb(harness.ProcessRunner, arguments =>
        {
            File.WriteAllText(OutputPathFrom(arguments), sarif);
            return new ProcessResult(0, string.Empty, string.Empty);
        });

        // Act
        CallToolResult result = await harness.Client.CallToolAsync("resharper_inspect", cancellationToken: Ct);

        // Assert — a successful result, truncated, carrying the inspect-only narrowing hint, unlogged.
        result.IsError.ShouldNotBe(true);
        string text = TextOf(result);
        text.ShouldContain("--- RESPONSE TRUNCATED ---");
        text.ShouldContain("Narrow the scan");
        harness.Logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListTools_CleanupRequiresFiles_InspectDoesNot()
    {
        // Arrange
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Act
        var tools = await harness.Client.ListToolsAsync(cancellationToken: Ct);

        // Assert — cleanup's files parameter is schema-required; inspect's stays optional.
        McpClientTool cleanup = tools.Single(tool => tool.Name == "resharper_cleanup");
        McpClientTool inspect = tools.Single(tool => tool.Name == "resharper_inspect");
        RequiredProperties(cleanup).ShouldContain("files");
        RequiredProperties(inspect).ShouldNotContain("files");
    }

    [Fact]
    public async Task Initialize_NegotiatedServerInfo_IdentifiesResharperCliMcp()
    {
        // Arrange / Act
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Assert — ServerInfo.Name identifies the server; the embedded instructions come across non-empty.
        harness.Client.ServerInfo.Name.ShouldBe("resharper-cli-mcp");
        harness.Client.ServerInstructions.ShouldNotBeNullOrEmpty();
    }

    private static string TextOf(CallToolResult result)
    {
        return result.Content.OfType<TextContentBlock>().First().Text;
    }

    private static void PlantSolution(FakeEnvironment environment, string fileName)
    {
        File.WriteAllText(Path.Combine(environment.CurrentDirectory, fileName), string.Empty);
    }

    /// <summary>The names in a tool's input-schema <c>required</c> array, or empty when it has none.</summary>
    private static IReadOnlyList<string> RequiredProperties(McpClientTool tool)
    {
        if (!tool.JsonSchema.TryGetProperty("required", out JsonElement required)
            || required.ValueKind != JsonValueKind.Array)
            return [];

        return required.EnumerateArray().Select(element => element.GetString()!).ToList();
    }

    /// <summary>
    ///     Routes the process-runner substitute by jb sub-command: the version probe succeeds, and an
    ///     <c>inspectcode</c> run is handed to <paramref name="onInspect" /> (which either writes SARIF to the
    ///     <c>-o=</c> path and returns success, or throws to simulate an unexpected failure). Everything else
    ///     succeeds with exit code 0.
    /// </summary>
    private static void RouteJb(IProcessRunner processRunner, Func<IReadOnlyList<string>, ProcessResult> onInspect)
    {
        processRunner
            .RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var arguments = callInfo.ArgAt<IReadOnlyList<string>>(1);

                if (arguments.Contains("--version")) return new ProcessResult(0, "Version: 2026.1.2", string.Empty);

                if (arguments.Count > 0 && arguments[0] == "inspectcode") return onInspect(arguments);

                return new ProcessResult(0, string.Empty, string.Empty);
            });
    }

    private static string OutputPathFrom(IReadOnlyList<string> arguments)
    {
        string arg = arguments.First(a => a.StartsWith("-o=", StringComparison.Ordinal));
        return arg["-o=".Length..];
    }
}