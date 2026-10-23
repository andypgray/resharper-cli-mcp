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
///     <see cref="GlobalCallToolFilter" /> end to end: the automated stand-in for a manual stdio smoke test.
/// </summary>
public sealed class GlobalCallToolFilterIntegrationTests
{
    /// <summary>Long enough that only a hang reaches it, short enough to fail rather than wedge.</summary>
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
        result.ShouldHaveFailed().ShouldBe("At least one file must be specified.");
        harness.Logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task CallTool_UnexpectedError_LogsExactlyOneWarningNamingTheTool()
    {
        // Arrange — jb is found (probe succeeds) and a solution is present, so the tool reaches the
        // inspectcode run; that run throws a non-UserError exception, which must escape the tool method.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        harness.Environment.PlantSolution("App.sln");
        RouteJb(harness.ProcessRunner, _ => throw new IOException("SARIF output target vanished"));

        // Act
        CallToolResult result = await harness.Client.CallToolAsync("resharper_inspect", cancellationToken: Ct);

        // Assert — surfaced as an error, and logged exactly once as a warning that names the tool.
        result.ShouldHaveFailed();
        LogEntry warning = harness.Logs.Warnings.ShouldHaveSingleItem();
        warning.Message.ShouldContain("resharper_inspect");
        warning.Category.ShouldBe(typeof(GlobalCallToolFilter).FullName);
        warning.Exception.ShouldBeOfType<IOException>();
    }

    [Fact]
    public async Task CallTool_SuccessOverBudget_TruncatesWithInspectHintAndLogsNothing()
    {
        // Arrange — a 40-token budget (100-char cap) against the 3-issue fixture forces truncation even
        // after progressive reduction: the reduction note alone is longer than 100 characters and counts
        // toward the fit check, so no reduced level can fit. The renderer therefore bottoms out at Minimal, a
        // single line with no newline before the cap, and the truncator cuts at the cap itself.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        harness.Environment.SetVariable("MAX_MCP_OUTPUT_TOKENS", "40");
        harness.Environment.PlantSolution("App.sln");
        string sarif = Fixtures.ReadSarif("inspect-sample.json");
        RouteJb(harness.ProcessRunner, arguments => JbStubs.Succeed(arguments, sarif));

        // Act
        CallToolResult result = await harness.Client.CallToolAsync("resharper_inspect", cancellationToken: Ct);

        // Assert — a successful result, truncated, carrying the inspect-only narrowing hint, unlogged.
        string text = result.ShouldHaveSucceeded();
        text.ShouldStartWith("Found 3 issue(s) across 2 file(s)."); // Minimal's header, cut at the cap not mid-word
        text.ShouldContain("--- RESPONSE TRUNCATED ---");
        text.ShouldContain("Narrow the scan");
        harness.Logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task CallTool_InspectOverBudget_ReducesDetailInsteadOfTruncating()
    {
        // Arrange — a 600-token budget (1,500-char cap) against a fixture shaped like a solution-wide run:
        // 24 issues of one rule with distinct messages across 2 files, plus 2 others. Listed in full it
        // overflows the budget; collapsing the repeated rule to one line per file brings it inside. This
        // proves the tool -> renderer -> filter wiring end to end.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        harness.Environment.SetVariable("MAX_MCP_OUTPUT_TOKENS", "600");
        harness.Environment.PlantSolution("App.sln");
        string sarif = Fixtures.ReadSarif("inspect-repetitive.json");
        RouteJb(harness.ProcessRunner, arguments => JbStubs.Succeed(arguments, sarif));

        // Act
        CallToolResult result = await harness.Client.CallToolAsync("resharper_inspect", cancellationToken: Ct);

        // Assert — reduced, not chopped: every issue still counted, every file still named, nothing logged.
        string text = result.ShouldHaveSucceeded();
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
        harness.Environment.PlantSolution("App.sln");
        string sarif = Fixtures.ReadSarif("inspect-sample.json");
        RouteJb(harness.ProcessRunner, arguments => JbStubs.Succeed(arguments, sarif));

        // Act
        CallToolResult result = await harness.Client.CallToolAsync("resharper_inspect", cancellationToken: Ct);

        // Assert — the id the filter opened the scope with reached both ends of the call: the config
        // resolution, and JbRunner several constructor hops down, neither of which has a parameter for it.
        result.ShouldHaveSucceeded();
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

        // And the pass the handshake settled (disabled here, so only its Debug outcome line) is under a
        // different id. Non-empty first, so a pass that stopped logging cannot make the second check vacuous.
        IReadOnlyList<LogEntry> warmerLines = LinesFrom(harness, typeof(CacheWarmer));
        warmerLines.ShouldNotBeEmpty();
        warmerLines
            .Select(entry => entry.ScopeValue(RunIdScope.PropertyName))
            .ShouldAllBe(runId => !Equals(runId, callRunId));

        harness.Logs.Warnings.ShouldBeEmpty();
    }

    private static IReadOnlyList<LogEntry> LinesFrom(McpPipelineHarness harness, Type category)
    {
        return harness.Logs.Entries.Where(entry => entry.Category == category.FullName).ToList();
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
    public async Task Initialize_NegotiatedServerInfo_CarriesTheIconAcrossTheHandshake()
    {
        // Arrange / Act
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Assert — the icon is only worth embedding if it survives serialization and the client's own
        // parse. ServerIdentityTests pins what goes in; this pins what a client is handed.
        Icon icon = harness.Client.ServerInfo.Icons.ShouldHaveSingleItem();
        icon.MimeType.ShouldBe("image/png");
        icon.Source.ShouldStartWith("data:image/png;base64,");
    }

    [Fact]
    public async Task CallTool_CancelledMidRun_StopsJbAndIsLoggedOnlyByTheSdk()
    {
        // Arrange — a jb run parked mid-analysis, so cancellation lands while the call is in flight. The
        // request carries an id of the test's own choosing, because cancelling a call the way a real client
        // does means naming the request to cancel.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        harness.Environment.PlantSolution("App.sln");
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
        await Should.ThrowAsync<OperationCanceledException>(() => call);
        (await stopped.Task.WaitAsync(Generous, Ct)).ShouldBeTrue();

        // And the one warning it costs is the SDK's own — it wraps every request handler in a catch that
        // logs before rethrowing, with no exception for cancellation. GlobalCallToolFilter excludes
        // OperationCanceledException from its logging catch and is not the source here; it runs inside that
        // handler, so there is nothing it can do about a warning logged outside it. Answering a
        // cancelled request with a CallToolResult would silence it and is not worth the lie.
        LogEntry warning = await harness.Logs.FirstWarning.WaitAsync(Generous, Ct);
        harness.Logs.Warnings.ShouldHaveSingleItem();
        warning.ShouldBeTheSdksFailedToolCall<OperationCanceledException>();
    }

    /// <summary>
    ///     Routes the substitute to an <c>inspectcode</c> that parks until its token is cancelled, standing in
    ///     for a long run. It reports through <paramref name="started" /> that it began, and completes
    ///     <paramref name="stopped" /> with <c>true</c> only once cancellation reached it, throwing as
    ///     <see cref="ProcessRunner" /> does after tree-killing <c>jb</c>.
    /// </summary>
    private static void RouteParkingJb(
        IProcessRunner processRunner,
        TaskCompletionSource started,
        TaskCompletionSource<bool> stopped)
    {
        processRunner
            .AnyRun()
            .Returns(async callInfo =>
            {
                IReadOnlyList<string> arguments = callInfo.Arguments();
                if (JbStubs.IsVersionProbe(arguments)) return JbStubs.VersionProbeAnswer;

                CancellationToken cancellationToken = callInfo.Token();
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
                return JbStubs.Success;
            });
    }

    /// <summary>
    ///     Routes the process-runner substitute by jb sub-command: the version probe succeeds, an
    ///     <c>inspectcode</c> run is handed to <paramref name="onInspect" />, and anything else is answered by
    ///     <see cref="JbStubs.Succeed" />.
    /// </summary>
    private static void RouteJb(IProcessRunner processRunner, Func<IReadOnlyList<string>, ProcessResult> onInspect)
    {
        processRunner
            .AnyRun()
            .Returns(callInfo =>
            {
                IReadOnlyList<string> arguments = callInfo.Arguments();

                if (JbStubs.IsVersionProbe(arguments)) return JbStubs.VersionProbeAnswer;

                return JbStubs.IsRunOf(arguments, "inspectcode")
                    ? onInspect(arguments)
                    : JbStubs.Succeed(arguments);
            });
    }
}