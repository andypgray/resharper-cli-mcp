using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Infrastructure;
using Zphil.ReSharperCli.Pipeline;
using Zphil.ReSharperCli.Services;
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
    /// <summary>Long enough that only a genuine hang reaches it, short enough to fail rather than wedge.</summary>
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

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
        // Arrange — a 40-token budget (100-char cap) against the 3-issue fixture forces truncation even
        // after progressive reduction. It cannot be reduced into that budget by construction: the reduction
        // note alone runs to roughly 215 characters and counts toward the fit check, so no reduced level can
        // ever fit 100. The renderer therefore bottoms out at Minimal — a single line with no newline before
        // the cap — and the truncator cuts at the cap itself.
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
        text.ShouldStartWith("Found 3 issue(s) across 2 file(s)."); // Minimal's header, cut at the cap not mid-word
        text.ShouldContain("--- RESPONSE TRUNCATED ---");
        text.ShouldContain("Narrow the scan");
        harness.Logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task CallTool_InspectOverBudget_ReducesDetailInsteadOfTruncating()
    {
        // Arrange — a 600-token budget (1,500-char cap) against a fixture shaped like the run that motivated
        // the ladder: 24 issues of one rule with distinct messages across 2 files, plus 2 others. Listed in
        // full that is ~3,000 characters; collapsing the repeated rule to one line per file brings it inside
        // the budget. This is the only end-to-end proof of the tool -> renderer -> filter wiring.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        harness.Environment.SetVariable("MAX_MCP_OUTPUT_TOKENS", "600");
        PlantSolution(harness.Environment, "App.sln");
        string sarif = Fixtures.ReadSarif("inspect-repetitive.json");
        RouteJb(harness.ProcessRunner, arguments =>
        {
            File.WriteAllText(OutputPathFrom(arguments), sarif);
            return new ProcessResult(0, string.Empty, string.Empty);
        });

        // Act
        CallToolResult result = await harness.Client.CallToolAsync("resharper_inspect", cancellationToken: Ct);

        // Assert — reduced, not chopped: every issue still counted, every file still named, nothing logged.
        result.IsError.ShouldNotBe(true);
        string text = TextOf(result);
        text.ShouldStartWith("Found 26 issue(s) across 2 file(s):");
        text.ShouldContain("--- DETAIL REDUCED ---");
        text.ShouldContain("x12, lines 13-24");
        text.ShouldNotContain("--- RESPONSE TRUNCATED ---");
        harness.Logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task CallTool_OneCall_TagsEveryLineItCausedWithOneRunId()
    {
        // Arrange — the reason the column exists: a pre-warm and a tool call overlap by design, and their
        // config, cache-state and run lines interleave in one shared daily file with nothing else to tell them
        // apart. The scope is opened at the outermost frame that knows a call has begun, so it has to reach
        // classes several layers down that never see it.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        PlantSolution(harness.Environment, "App.sln");
        string sarif = Fixtures.ReadSarif("inspect-sample.json");
        RouteJb(harness.ProcessRunner, arguments =>
        {
            File.WriteAllText(OutputPathFrom(arguments), sarif);
            return new ProcessResult(0, string.Empty, string.Empty);
        });

        // Act
        CallToolResult result = await harness.Client.CallToolAsync("resharper_inspect", cancellationToken: Ct);

        // Assert — the id the filter opened the scope with reached both ends of the call: the config
        // resolution, and JbRunner four constructor hops down, neither of which has a parameter for it.
        result.IsError.ShouldNotBe(true);
        IReadOnlyList<LogEntry> filterLines = LinesFrom(harness, typeof(GlobalCallToolFilter));
        filterLines.ShouldNotBeEmpty();

        object? callRunId = filterLines[0].ScopeValue(RunIdScope.PropertyName);
        callRunId.ShouldNotBeNull();
        List<string> underTheCall = harness.Logs.Entries
            .Where(entry => Equals(entry.ScopeValue(RunIdScope.PropertyName), callRunId))
            .Select(entry => entry.Category)
            .ToList();

        underTheCall.ShouldContain(typeof(ConfigResolver).FullName!);
        underTheCall.ShouldContain(typeof(JbRunner).FullName!);

        // And the pre-warm pass the handshake triggered is under a *different* id, which is the whole reason
        // for having one: the two overlap by design and their lines interleave in one file.
        LinesFrom(harness, typeof(CacheWarmer))
            .Select(entry => entry.ScopeValue(RunIdScope.PropertyName))
            .ShouldAllBe(runId => !Equals(runId, callRunId));

        harness.Logs.Warnings.ShouldBeEmpty();
    }

    private static IReadOnlyList<LogEntry> LinesFrom(McpPipelineHarness harness, Type category)
    {
        return harness.Logs.Entries.Where(entry => entry.Category == category.FullName).ToList();
    }

    [Fact]
    public async Task ListTools_CleanupRequiresFiles_InspectDoesNot()
    {
        // Arrange
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Act
        IList<McpClientTool> tools = await harness.Client.ListToolsAsync(cancellationToken: Ct);

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

    [Fact]
    public async Task CallTool_CancelledMidRun_StopsJbAndIsLoggedOnlyByTheSdk()
    {
        // Arrange — a jb run parked mid-analysis, so cancellation lands while the call is genuinely in
        // flight. The request carries an id of the test's own choosing, because cancelling a call the way a
        // real client does means naming the request to cancel.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        PlantSolution(harness.Environment, "App.sln");
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RouteParkingJb(harness.ProcessRunner, started, stopped);

        RequestId requestId = new("cancel-me");
        Task<CallToolResult> call = harness.Client.SendRequestAsync<CallToolRequestParams, CallToolResult>(
            "tools/call",
            new CallToolRequestParams { Name = "resharper_inspect" },
            requestId: requestId,
            cancellationToken: Ct).AsTask();
        await started.Task.WaitAsync(Generous, Ct);

        // Act — what an interrupted client sends. Cancelling the client's own token does not do this: the
        // SDK's client never emits notifications/cancelled, so it would abandon the call locally and leave
        // the server running to completion, which is not the situation this pins.
        await harness.Client.SendNotificationAsync(
            "notifications/cancelled",
            new CancelledNotificationParams { RequestId = requestId, Reason = "user interrupted" },
            cancellationToken: Ct);

        // Assert — the request fails rather than answering, which is the correct protocol outcome, and the
        // cancellation reached all the way down: jb is stopped rather than left running behind an
        // abandoned call.
        (await Should.ThrowAsync<OperationCanceledException>(() => call)).ShouldNotBeNull();
        (await stopped.Task.WaitAsync(Generous, Ct)).ShouldBeTrue();

        // And the one warning it costs is the SDK's own — it wraps every request handler in a catch that
        // logs before rethrowing, with no exception for cancellation. GlobalCallToolFilter excludes
        // OperationCanceledException from its logging catch on purpose and is not the source here; it runs
        // inside that handler, so there is nothing it can do about a warning logged outside it. Answering a
        // cancelled request with a CallToolResult would silence it and is not worth the lie.
        LogEntry warning = await harness.Logs.FirstWarning.WaitAsync(Generous, Ct);
        harness.Logs.Warnings.ShouldHaveSingleItem();
        warning.Category.ShouldStartWith("ModelContextProtocol.");
        warning.Message.ShouldContain("request handler failed");
        warning.Exception.ShouldBeAssignableTo<OperationCanceledException>();
    }

    /// <summary>
    ///     An <c>inspectcode</c> that parks until its token is cancelled, exactly as a long run looks, and
    ///     reports through <paramref name="started" /> that it began — completing it with <c>true</c> only
    ///     once cancellation actually reached it, which is what <see cref="ProcessRunner" /> surfaces after
    ///     tree-killing <c>jb</c>.
    /// </summary>
    private static void RouteParkingJb(
        IProcessRunner processRunner,
        TaskCompletionSource started,
        TaskCompletionSource<bool> stopped)
    {
        processRunner
            .RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var arguments = callInfo.ArgAt<IReadOnlyList<string>>(1);
                if (arguments.Contains("--version")) return new ProcessResult(0, "Version: 2026.1.2", string.Empty);

                var cancellationToken = callInfo.ArgAt<CancellationToken>(3);
                started.TrySetResult();

                try
                {
                    // Bounded, so a regression that stopped propagating cancellation fails this test rather
                    // than wedging the run.
                    await Task.Delay(Generous, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    stopped.TrySetResult(true);
                    throw;
                }

                stopped.TrySetResult(false);
                return new ProcessResult(0, string.Empty, string.Empty);
            });
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