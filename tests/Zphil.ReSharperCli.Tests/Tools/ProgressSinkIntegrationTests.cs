using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.Core;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Tests.Tools;

/// <summary>
///     Drives a real MCP client watching a long run advance: the whole path, from <c>jb</c>'s stdout through
///     the run state and the formatter to <c>notifications/progress</c> on the wire.
/// </summary>
/// <remarks>
///     <see cref="ProgressSinkTests" /> pins the sink's own ordering. This class catches what a unit test
///     cannot see: that the SDK binds the <c>RequestContext</c> parameter without advertising it in the
///     schema, that the token round-trips, and that nothing reports against a request already answered.
///     Ordering and nothing-after-the-result are read off the server's own output stream, never off the
///     client: a beat sent after the result is dropped before any client handler sees it, and the order the
///     client's handlers run in is not the order the server wrote in. See <see cref="WireLog" />.
/// </remarks>
public sealed class ProgressSinkIntegrationTests
{
    /// <summary>Long enough that only a hang reaches it, short enough to fail rather than wedge.</summary>
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    ///     One long run, and everything the wire has to get right about it. Deliberately one test rather
    ///     than one per assertion: every assertion is about one conversation's ordering, notifications before
    ///     the result and nothing after it, so splitting it would re-arrange the same parked run for each.
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
        // heartbeat however brisk the harness has made it. Two beats, so the heartbeat is repeating rather
        // than the client having heard a single opening message.
        Task<CallToolResult> call = harness.Client
            .CallToolAsync(ResharperTools.InspectToolName, progress: recorder, cancellationToken: Ct)
            .AsTask();

        try
        {
            await recorder.WaitUntilAsync(
                () => recorder.Count >= 2 && recorder.Messages.Any(message => message.Contains("analyzing 2 files")),
                "a repeat beat naming the analysed files",
                Ct);
        }
        finally
        {
            // In a finally, so a failed wait does not leave the stubbed jb parked on its run lease.
            release.SetResult();
        }

        CallToolResult result = await call.WaitAsync(Generous, Ct);
        IReadOnlyList<Heard> atCompletion = recorder.Items;

        // Assert — every message names the run, which is what makes a stuck one tellable from a slow one.
        result.ShouldHaveSucceeded();
        recorder.Messages.ShouldAllBe(message => message.Contains("inspectcode on App.slnx: "));

        // Monotonic, the one thing the protocol asks of `progress`, and read off the wire. ProgressSink numbers
        // a notification inside a chain where each send awaits the one before it, so the wire can be held to
        // more than sorted: contiguous, and based at one. The lower bound goes first so a tap that captured
        // nothing fails there rather than satisfying the pin with an empty list, and it is `>=` rather than
        // `==` because a frame written but never delivered is the bug the window below exists to catch.
        IReadOnlyList<double?> written = harness.Wire.ProgressValues;
        written.Count.ShouldBeGreaterThanOrEqualTo(atCompletion.Count);
        written.ShouldBe(Enumerable.Range(1, written.Count).Select(counter => (double?)counter));

        // No `total`, because elapsed-against-cap renders as a filling bar meaning "budget consumed" rather
        // than "work done".
        atCompletion.ShouldAllBe(heard => heard.Total == null);

        // Order-free, and about the client rather than the server: nothing was delivered twice.
        atCompletion.Select(heard => heard.Progress).ShouldBeUnique();

        // And nothing reported after the result went out, also off the wire. What puts the last frame ahead of
        // the result is the sink's drain, which the tool method awaits before its answer is serialized. The
        // window can be generous because it is the wire: a correct run writes no frame into it however loaded
        // the machine is, and several would-be beats fit in it at the harness's interval.
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        harness.Wire.LastProgressIndex.ShouldBeLessThan(harness.Wire.ToolResultIndex);
    }

    [Fact]
    public async Task CallTool_AResetQueuedBehindAnotherRun_HearsItsWaitAndNothingAfterTheResult()
    {
        // Arrange — a caller on the run lock with nothing to stream: a reset spawns no jb, so the stub answers
        // the version probe discovery makes and nothing else. The held lock file is another session's live jb
        // as far as this server can tell.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        string cacheHome = harness.Environment.CreateTempDirectory();
        harness.Environment.SetVariable("JB_CACHE_HOME", cacheHome);
        string solutionPath = harness.Environment.PlantSolution("App.slnx");
        CacheHomes.PlantGenerationFor(cacheHome, solutionPath);
        RouteJb(harness.ProcessRunner, _ => Task.CompletedTask);

        FileStream held = CacheHomes.HoldLockFile(cacheHome, solutionPath);
        Recorder recorder = new();

        // Act — the wait has to outlast JbRunLock.NotableWait before a beat names another run, so this waits
        // it out rather than settling for the immediate "starting".
        Task<CallToolResult> call = harness.Client
            .CallToolAsync(ResharperTools.ResetCacheToolName, progress: recorder, cancellationToken: Ct)
            .AsTask();

        try
        {
            await recorder.FirstAsync(
                heard => heard.Message.Contains("waiting for another run"),
                "a beat naming the queue wait",
                Ct);
        }
        finally
        {
            // Released before the call is awaited, because the reset is queued on this lock and cannot finish
            // until the file is let go; and in a finally, so a failed wait does not leave the call parked
            // behind a handle nothing releases.
            await held.DisposeAsync();
        }

        CallToolResult result = await call.WaitAsync(Generous, Ct);

        // Read before the wire is snapshotted, so the "at least what the client heard" bound below cannot be
        // beaten by a frame written between the two reads.
        int heardByTheClient = recorder.Count;

        // Assert — the wait reached the client, labelled as this tool's own work rather than as a jb
        // subcommand, and with no cap in it, since a reset spends none of the run budget.
        result.ShouldHaveSucceeded();
        recorder.Messages.ShouldAllBe(message => message.Contains("cache reset on App.slnx: "));
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

    /// <summary>
    ///     The wait no cache can explain. Two solutions are two cache generations, so the run lock lets both
    ///     calls straight through; what serializes them is the per-server bound on <c>jb</c> processes, and
    ///     this is the only channel through which the caller sitting behind the other one ever hears about it.
    /// </summary>
    [Fact]
    public async Task CallTool_TwoCallsAgainstTwoSolutions_TheSecondHearsItWaitingForTheFirst()
    {
        // Arrange
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        string first = harness.Environment.CreateCheckout("First.slnx");
        string second = harness.Environment.CreateCheckout("Second.slnx");

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RouteStreamingJb(harness.ProcessRunner, release.Task);

        Recorder holding = new();
        Recorder waiting = new();

        // Act — the first call is watched too, and only for its ordering: two calls issued back to back race
        // to reach the server, so "this one is the holder" has to be observed rather than assumed. A beat
        // naming its analysed files is proof its jb is running and the slot is taken.
        Task<CallToolResult> held = Call(harness, first, holding);
        Task<CallToolResult> queued;

        try
        {
            await holding.FirstAsync(
                heard => heard.Message.Contains("analyzing 2 files"),
                "the first call's run to be under way and holding the slot",
                Ct);

            queued = Call(harness, second, waiting);

            // The wait has to outlast JbRunLock.NotableWait before a beat names the run ahead, so this waits
            // it out rather than settling for the immediate "starting".
            await waiting.FirstAsync(
                heard => heard.Message.Contains("waiting for this server's other jb run"),
                "a beat naming the wait for this server's other run",
                Ct);
        }
        finally
        {
            // Released in a finally: the queued call is behind the held one, so a failed wait would otherwise
            // leave both parked on a source nothing completes.
            release.SetResult();
        }

        CallToolResult heldResult = await held.WaitAsync(Generous, Ct);
        CallToolResult queuedResult = await queued.WaitAsync(Generous, Ct);

        // Assert — both calls got what they came for, in order, and the second's messages are about its own
        // solution rather than the one it waited behind.
        heldResult.ShouldHaveSucceeded();
        queuedResult.ShouldHaveSucceeded();
        waiting.Messages.ShouldAllBe(message => message.Contains("inspectcode on Second.slnx: "));

        // No cap in that clause: nothing is armed while a call waits for the slot, so charging the wait
        // against the run budget would send a caller to raise the one number that was not the problem.
        waiting.Messages
            .Where(message => message.Contains("waiting for this server's other jb run"))
            .ShouldAllBe(message => !message.Contains("cap"));

        // Two conversations wrote to this wire, so its counter is not read back here — the single-call tests
        // above own that contract. What is still true is that the server wrote at least what both clients
        // heard.
        harness.Wire.ProgressValues.Count.ShouldBeGreaterThanOrEqualTo(holding.Count + waiting.Count);
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

        // Assert — both names the parameter could carry, so a rename cannot silently start advertising it and
        // leave a pin passing on a name it no longer uses.
        result.ShouldHaveSucceeded();
        harness.Logs.Warnings.ShouldBeEmpty();

        IReadOnlyList<string> progressParameters = tools
            .SelectMany(tool => tool.PropertyNames()
                .Where(property => property is "progress" or "context")
                .Select(property => $"{tool.Name}.{property}"))
            .ToList();

        progressParameters.ShouldBeEmpty();
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

    /// <summary>Starts one watched <c>resharper_inspect</c> call against a named solution.</summary>
    private static Task<CallToolResult> Call(McpPipelineHarness harness, string solutionPath, Recorder progress)
    {
        return harness.Client
            .CallToolAsync(
                ResharperTools.InspectToolName,
                new Dictionary<string, object?> { ["solutionPath"] = solutionPath },
                progress,
                cancellationToken: Ct)
            .AsTask();
    }

    /// <summary>
    ///     Routes every stubbed <c>jb</c> here the same way (probe answered, SARIF written, generation
    ///     planted) around <paramref name="duringRun" />, where each test does its streaming or watching.
    /// </summary>
    private static void RouteJb(IProcessRunner processRunner, Func<CallInfo, Task> duringRun)
    {
        processRunner
            .AnyRun()
            .Returns(async callInfo =>
            {
                IReadOnlyList<string> arguments = callInfo.Arguments();
                if (JbStubs.IsVersionProbe(arguments)) return JbStubs.VersionProbeAnswer;

                await duringRun(callInfo);

                JbStubs.WriteEmptySarifIfRequested(arguments);
                return JbStubs.Succeed(arguments);
            });
    }

    /// <summary>
    ///     Routes a <c>jb</c> that streams the analysis vocabulary a real one does, then parks until
    ///     <paramref name="release" /> completes, so a test controls when the run ends and can assert on what
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

    /// <summary>Routes a <c>jb</c> that records each run's line observer, and otherwise succeeds.</summary>
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

    /// <summary>Represents one notification as the client heard it, in a form a failure message prints.</summary>
    private sealed record Heard(float Progress, float? Total, string Message);

    /// <summary>Records the client end of the channel: everything the server sent, in arrival order.</summary>
    private sealed class Recorder() : RecordingSink<Heard>(Generous), IProgress<ProgressNotificationValue>
    {
        public IReadOnlyList<string> Messages => Items.Select(heard => heard.Message).ToList();

        public void Report(ProgressNotificationValue value)
        {
            Record(new Heard(value.Progress, value.Total, value.Message ?? ""));
        }
    }
}