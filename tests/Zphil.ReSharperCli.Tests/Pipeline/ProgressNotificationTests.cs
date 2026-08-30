using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.Core;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestSupport;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Tests.Pipeline;

/// <summary>
///     A real MCP client watching a long run advance: the whole path, from <c>jb</c>'s stdout through the run
///     state and the formatter to <c>notifications/progress</c> on the wire.
/// </summary>
/// <remarks>
///     What this exists to catch is everything a unit test cannot see — that the SDK really does bind the
///     <c>RequestContext</c> parameter without advertising it in the schema, that the token round-trips, and
///     that nothing reports against a request that has already been answered. That last one is read off the
///     server's own output stream rather than off the client, and could never have been read off the client: a
///     beat sent after the result is dropped before any handler sees it, so a correct observation and a buggy
///     one look identical from there — and the order the client's handler runs in is not the order the server
///     wrote in either. See <see cref="WireLog" />.
/// </remarks>
public sealed class ProgressNotificationTests
{
    /// <summary>Long enough that only a genuine hang reaches it, short enough to fail rather than wedge.</summary>
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    ///     One long run, and everything the wire has to get right about it. Deliberately one test rather
    ///     than four: every assertion is about one conversation's ordering — notifications before the
    ///     result, nothing after it — so four tests would re-arrange the same parked run four times.
    /// </summary>
    [Fact]
    public async Task CallTool_AClientThatAsksForProgress_HearsTheRunAdvanceAndNothingAfterItEnds()
    {
        // Arrange — a jb that reports files as it goes and then parks until the test lets it finish, so
        // "notifications arrived before the result" is a fact rather than a race.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        harness.Environment.PlantSolution("App.slnx");

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RouteStreamingJb(harness.ProcessRunner, release.Task);

        Recorder recorder = new();

        // Act — the SDK puts a token in the request _meta exactly when a progress handler is supplied. The
        // run is held open until a beat has named the streamed files, so the release cannot outrun the
        // heartbeat however brisk the harness has made it.
        Task<CallToolResult> call = harness.Client
            .CallToolAsync(ResharperTools.InspectToolName, progress: recorder, cancellationToken: Ct)
            .AsTask();

        await recorder.WaitUntilAsync(
            () => recorder.Count >= 2 && recorder.Messages.Any(message => message.Contains("analyzing 2 files")),
            "a repeat beat naming the analysed files",
            Ct);
        release.SetResult();
        CallToolResult result = await call.WaitAsync(Generous, Ct);
        IReadOnlyList<ProgressNotificationValue> atCompletion = recorder.Items;

        // Assert — the run advanced more than once, so a heartbeat is genuinely repeating rather than the
        // client having heard a single opening message.
        result.IsError.ShouldNotBe(true);
        atCompletion.Count.ShouldBeGreaterThanOrEqualTo(2);

        // Every message names the run, which is what makes a stuck one tellable from a slow one.
        recorder.Messages.ShouldAllBe(message => message.Contains("inspectcode on App.slnx: "));
        recorder.Messages.ShouldContain(message => message.Contains("analyzing 2 files"));

        // Monotonic, which is the one thing the protocol asks of `progress` — read off the wire, because what
        // the client's list records is the order thread-pool callbacks ran in, not the order the server wrote
        // in. This is a promise rather than a hope: ProgressSink numbers a notification inside a chain where
        // each send awaits the one before it, so the wire can be held to more than "sorted" — contiguous, and
        // based at one. The lower bound goes first so a tap that captured nothing fails there rather than
        // satisfying the pin with an empty list, and it is `>=` rather than `==` because a frame written but
        // never delivered is the bug the window below exists to catch.
        IReadOnlyList<double?> written = harness.Wire.ProgressValues;
        written.Count.ShouldBeGreaterThanOrEqualTo(atCompletion.Count);
        written.ShouldBe(Enumerable.Range(1, written.Count).Select(counter => (double?)counter));

        // No `total`, because elapsed-against-cap renders as a filling bar meaning "budget consumed" rather
        // than "work done".
        atCompletion.ShouldAllBe(value => value.Total == null);

        // Order-free, and about the client rather than the server: nothing was delivered twice.
        atCompletion.Select(value => value.Progress).Distinct().Count().ShouldBe(atCompletion.Count);

        // And nothing reported after the result went out — also off the wire, where a frame's position is the
        // order the server wrote it in. A late report goes against a request that has already been answered, so
        // the client has torn down its handler registration by then and no client-side count can ever see one.
        // What puts the last frame ahead of the result is the sink's drain, which the tool method awaits before
        // its answer is serialized. The window can be generous precisely because it is the wire: a correct run
        // writes no frame into it however loaded the machine is. Ten would-be beats fit in it at the harness's
        // interval.
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        harness.Wire.LastProgressIndex.ShouldBeLessThan(harness.Wire.ToolResultIndex);
    }

    [Fact]
    public async Task CallTool_AResetQueuedBehindAnotherRun_HearsItsWaitAndNothingAfterTheResult()
    {
        // Arrange — the other caller on the run lock, and the one with nothing to stream: a reset spawns no
        // jb, so the stub answers the version probe discovery makes and nothing else. The held lock file is
        // another session's live jb as far as this server can tell.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        string cacheHome = harness.Environment.CreateTempDirectory();
        harness.Environment.SetVariable("JB_CACHE_HOME", cacheHome);
        string solutionPath = harness.Environment.PlantSolution("App.slnx");
        CacheHomes.PlantGenerationFor(cacheHome, solutionPath);
        RouteJb(harness.ProcessRunner, _ => Task.CompletedTask);

        FileStream held = CacheHomes.HoldLockFile(cacheHome, solutionPath);
        Recorder recorder = new();

        // Act — the wait has to outlast JbRunLock.NotableWait before a beat names another run, so this waits
        // the second out rather than settling for the immediate "starting".
        Task<CallToolResult> call = harness.Client
            .CallToolAsync(ResharperTools.ResetCacheToolName, progress: recorder, cancellationToken: Ct)
            .AsTask();

        try
        {
            await recorder.WaitUntilAsync(
                () => recorder.Messages.Any(message => message.Contains("waiting for another run")),
                "a beat naming the queue wait",
                Ct);
        }
        finally
        {
            // Released on every path rather than at scope exit: the reset is queued on this very lock, so a
            // failed wait would otherwise leave the call parked behind a file nothing was going to let go of.
            await held.DisposeAsync();
        }

        CallToolResult result = await call.WaitAsync(Generous, Ct);

        // Read before the wire is snapshotted, so the "at least what the client heard" bound below cannot be
        // beaten by a frame written between the two reads.
        int heardByTheClient = recorder.Count;

        // Assert — the wait reached the client, labelled as this tool's own work rather than as a jb
        // subcommand, and with no cap in it, since a reset spends none of the run budget.
        result.IsError.ShouldNotBe(true);
        recorder.Messages.ShouldAllBe(message => message.Contains("cache reset on App.slnx: "));
        recorder.Messages.ShouldContain(message => message.Contains("waiting for another run"));
        recorder.Messages.ShouldAllBe(message => !message.Contains("cap"));

        // The counter, off the wire and held to the same contiguous-from-one contract as the run above, behind
        // the same bound that stops a dead tap satisfying it with an empty list.
        IReadOnlyList<double?> written = harness.Wire.ProgressValues;
        written.Count.ShouldBeGreaterThanOrEqualTo(heardByTheClient);
        written.ShouldBe(Enumerable.Range(1, written.Count).Select(counter => (double?)counter));

        // And nothing after the result. The reporter is scoped to the acquire, so it is already gone by the
        // time the deletes run, let alone by the time the response goes out.
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        harness.Wire.LastProgressIndex.ShouldBeLessThan(harness.Wire.ToolResultIndex);
    }

    [Fact]
    public async Task CallTool_AClientThatWantsNoProgress_GetsAnUnchangedResultAndAnUnchangedSchema()
    {
        // Arrange — a call with no progress token still runs, and the parameter that carries the channel stays
        // out of the advertised schema. A tool that started advertising it would be asking a model to pass one.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        harness.Environment.PlantSolution("App.slnx");
        RouteStreamingJb(harness.ProcessRunner, Task.CompletedTask);

        // Act
        IList<McpClientTool> tools = await harness.Client.ListToolsAsync(cancellationToken: Ct);
        CallToolResult result = await harness.Client.CallToolAsync(ResharperTools.InspectToolName, cancellationToken: Ct);

        // Assert — both spellings the parameter has had, so a rename cannot quietly start advertising it and
        // leave a pin passing on the name it no longer uses.
        result.IsError.ShouldNotBe(true);
        harness.Logs.Warnings.ShouldBeEmpty();

        foreach (McpClientTool tool in tools)
        {
            IReadOnlyList<string> properties = PropertyNames(tool);

            properties.ShouldNotContain("progress");
            properties.ShouldNotContain("context");
        }
    }

    [Fact]
    public async Task PreWarm_ReportsNothing_BecauseNobodyIsWaitingOnIt()
    {
        // Arrange — the pre-warm's contract is that it can neither delay nor fail nor report through a call.
        // It runs on the connection, not on a tools/call, so it has no progress token to report against and
        // must never be handed a line observer either.
        List<Action<string>?> observers = [];
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(
            Ct,
            true,
            (environment, processRunner) =>
            {
                environment.PlantSolution("App.slnx");
                RouteRecordingJb(processRunner, observers);
            });

        // Act
        await harness.Warmer.Finished.WaitAsync(Generous, Ct);

        // Assert — a run happened, and no run in it was watched.
        harness.Warmer.Outcome.ShouldBe(WarmUpOutcome.Warmed);
        observers.ShouldNotBeEmpty();
        observers.ShouldAllBe(observer => observer == null);
    }

    /// <summary>
    ///     The routing every stubbed <c>jb</c> here shares — probe answered, SARIF written, generation
    ///     planted — around <paramref name="duringRun" />, where each test does its streaming or watching.
    /// </summary>
    private static void RouteJb(IProcessRunner processRunner, Func<CallInfo, Task> duringRun)
    {
        processRunner
            .AnyRun()
            .Returns(async callInfo =>
            {
                var arguments = callInfo.ArgAt<IReadOnlyList<string>>(1);
                if (JbStubs.IsVersionProbe(arguments)) return JbStubs.VersionProbeAnswer;

                await duringRun(callInfo);

                JbStubs.WriteEmptySarifIfRequested(arguments);
                CacheHomes.PlantGenerationFromJbRun(arguments);

                return new ProcessResult(0, string.Empty, string.Empty);
            });
    }

    /// <summary>
    ///     A <c>jb</c> that streams the analysis vocabulary a real one does, then parks until
    ///     <paramref name="release" /> completes — so a test controls when the run ends and can assert on what
    ///     the client heard before that.
    /// </summary>
    private static void RouteStreamingJb(IProcessRunner processRunner, Task release)
    {
        RouteJb(processRunner, async callInfo =>
        {
            if (callInfo.OutputLineObserver() is { } onLine)
            {
                onLine("JetBrains Inspect Code 2026.2.1");
                onLine(JbProgressLines.AnalyzingPhaseLine);
                onLine("Analyzing A.cs");
                onLine("Analyzing B.cs");
            }

            await release;
        });
    }

    /// <summary>A <c>jb</c> that records the line observer each run was handed, and otherwise succeeds.</summary>
    private static void RouteRecordingJb(IProcessRunner processRunner, List<Action<string>?> observers)
    {
        RouteJb(processRunner, callInfo =>
        {
            lock (observers)
            {
                observers.Add(callInfo.OutputLineObserver());
            }

            return Task.CompletedTask;
        });
    }

    private static IReadOnlyList<string> PropertyNames(McpClientTool tool)
    {
        if (!tool.JsonSchema.TryGetProperty("properties", out JsonElement properties)) return [];

        return properties.EnumerateObject().Select(property => property.Name).ToList();
    }

    /// <summary>The client end of the channel: everything the server sent, in the order it arrived.</summary>
    private sealed class Recorder() : RecordingSink<ProgressNotificationValue>(Generous), IProgress<ProgressNotificationValue>
    {
        public IReadOnlyList<string> Messages => Items.Select(value => value.Message ?? "").ToList();

        public void Report(ProgressNotificationValue value)
        {
            Record(value);
        }
    }
}