using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     The heartbeat: that it beats at once, keeps beating, survives a sink that throws, and — the one that
///     matters most — stops dead at disposal.
/// </summary>
/// <remarks>
///     The last of those is not tidiness. The sink ends at the MCP session's <c>TokenProgress.Report</c>,
///     which discards the task it sends on, so a beat issued against a request that has already been answered
///     faults where only <c>TaskScheduler.UnobservedTaskException</c> would ever see it.
/// </remarks>
public sealed class JbRunProgressTests
{
    /// <summary>Short enough that a test sees several beats, long enough not to be flaky under load.</summary>
    private static readonly TimeSpan Brisk = TimeSpan.FromMilliseconds(40);

    /// <summary>Long enough that only a genuine hang reaches it.</summary>
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan Cap = TimeSpan.FromMinutes(10);

    private static readonly string SolutionPath = Path.Combine("C:", "repo", "App.slnx");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void HeartbeatInterval_IsTheTenSecondsTheDocsPromise()
    {
        // The README, the setup guide and the CHANGELOG all say "every ten seconds". The number carries no
        // protocol meaning and could be moved, but it cannot be moved quietly: it is the one fact about this
        // feature a reader can check against a stopwatch.
        JbRunProgress.HeartbeatInterval.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Constructor_BeatsImmediately_RatherThanWaitingOutTheFirstInterval()
    {
        // Arrange — the silence this exists to break starts the moment the call arrives, so the first beat
        // cannot wait an interval. With a ten-second interval in production, one that did would leave the
        // caller with exactly the dark stretch the feature is for.
        Sink sink = new();

        // Act
        await using JbRunProgress progress = Build(sink, TimeSpan.FromMinutes(5));
        await sink.WaitForAsync(1, Ct);

        // Assert
        JbRunProgressSnapshot first = sink.Items.ShouldHaveSingleItem();
        first.Phase.ShouldBe(JbRunPhase.Queued);
        first.Subcommand.ShouldBe("inspectcode");
        first.Cap.ShouldBeNull();
    }

    [Fact]
    public async Task Beats_KeepComing_WhileTheRunIsStillGoing()
    {
        // Arrange
        Sink sink = new();

        // Act
        await using JbRunProgress progress = Build(sink);

        // Assert — a run in flight and a run that has hung must not look the same, which takes more than one
        // beat to establish.
        await sink.WaitForAsync(3, Ct);
    }

    [Fact]
    public async Task Beat_SinkThrows_KeepsBeatingRatherThanTakingTheProcessDown()
    {
        // Arrange — this runs on a timer thread, where an escaping exception has no caller to reach.
        Sink sink = new() { Throw = true };

        // Act
        await using JbRunProgress progress = Build(sink);

        // Assert — it was called repeatedly, so the first throw neither stopped the timer nor unwound.
        await sink.WaitForAsync(3, Ct);
    }

    [Fact]
    public async Task DisposeAsync_NothingBeatsAfterIt()
    {
        // Arrange — a sink that dawdles, so disposal reliably overlaps a beat that is already in flight
        // rather than only ever one that has not started.
        Sink sink = new() { Dawdle = TimeSpan.FromMilliseconds(30) };
        JbRunProgress progress = Build(sink);
        await sink.WaitForAsync(2, Ct);

        // Act
        await progress.DisposeAsync();
        int atDisposal = sink.Count;

        // Assert — the flag stops a beat that has not started and the awaited timer disposal waits out one
        // that has, so the count cannot move again however long we watch.
        await Task.Delay(Brisk * 10, Ct);
        sink.Count.ShouldBe(atDisposal);
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_IsHarmless()
    {
        Sink sink = new();
        JbRunProgress progress = Build(sink);

        await progress.DisposeAsync();
        await progress.DisposeAsync();
    }

    [Fact]
    public async Task OnOutputLine_AfterDisposal_IsIgnored()
    {
        // Arrange — ProcessRunner can abandon a live reader at the cap, so a line genuinely arrives after the
        // run it describes has thrown. It has to be harmless rather than merely unlikely.
        Sink sink = new();
        JbRunProgress progress = Build(sink);
        await progress.DisposeAsync();

        // Act
        progress.OnOutputLine("Analyzing files");
        progress.OnOutputLine("Analyzing A.cs");

        // Assert
        progress.FilesSeen.ShouldBe(0);
    }

    [Fact]
    public async Task OnOutputLine_TheAnalysisSweep_CountsFilesWithinThePhase()
    {
        // Arrange
        Sink sink = new();
        await using JbRunProgress progress = Build(sink);
        progress.Spawning("cold (none on disk)");

        // Act
        progress.OnOutputLine("Analyzing files");
        progress.OnOutputLine("Analyzing A.cs");
        progress.OnOutputLine("Analyzing B.cs");
        progress.OnOutputLine("JetBrains Inspect Code 2026.2.1");

        // Assert
        progress.FilesSeen.ShouldBe(2);
        JbRunProgressSnapshot latest = await sink.NextAfterAsync(sink.Count, Ct);
        latest.Phase.ShouldBe(JbRunPhase.Analyzing);
        latest.FilesSeen.ShouldBe(2);
    }

    [Fact]
    public async Task OnOutputLine_TheSecondSweep_RestartsTheCountRatherThanCarryingIt()
    {
        // Arrange — jb's two sweeps report different totals for the same solution (1,332 analysed against 882
        // inspected on one measured run), so a running total would be a number matching nothing jb said.
        Sink sink = new();
        await using JbRunProgress progress = Build(sink);

        // Act
        progress.OnOutputLine("Analyzing files");
        progress.OnOutputLine("Analyzing A.cs");
        progress.OnOutputLine("Analyzing B.cs");
        progress.OnOutputLine("Running inspections");
        progress.OnOutputLine("Inspecting A.cs");

        // Assert
        progress.FilesSeen.ShouldBe(1);
        JbRunProgressSnapshot latest = await sink.NextAfterAsync(sink.Count, Ct);
        latest.Phase.ShouldBe(JbRunPhase.Inspecting);
        latest.FilesSeen.ShouldBe(1);
    }

    [Fact]
    public async Task Spawning_ArmsTheCapAndCarriesTheCacheState()
    {
        // Arrange — before the spawn a call is queueing, and queue time is outside the run budget: naming the
        // cap then would send a caller to raise the one number that was not the problem.
        Sink sink = new();
        await using JbRunProgress progress = Build(sink);
        await sink.WaitForAsync(1, Ct);
        sink.Items[0].Cap.ShouldBeNull();

        // Act
        progress.Spawning("warm (14m old marker, _App.123.00)");

        // Assert
        JbRunProgressSnapshot afterSpawn = await sink.NextAfterAsync(sink.Count, Ct);
        afterSpawn.Phase.ShouldBe(JbRunPhase.Starting);
        afterSpawn.Cap.ShouldBe(Cap);
        afterSpawn.CacheSummary.ShouldBe("warm (14m old marker, _App.123.00)");
    }

    [Fact]
    public async Task Seeding_IsItsOwnPhase_SoACopyThatTakesMinutesIsNotReportedAsAQueueWait()
    {
        // Arrange
        Sink sink = new();
        await using JbRunProgress progress = Build(sink);

        // Act
        progress.Seeding();

        // Assert — still no cap, because no jb exists yet; a transplant runs inside the call and before it.
        JbRunProgressSnapshot seeding = await sink.NextAfterAsync(sink.Count, Ct);
        seeding.Phase.ShouldBe(JbRunPhase.Seeding);
        seeding.Cap.ShouldBeNull();
    }

    [Fact]
    public void Reporting_NoSink_BuildsNoReporterAtAll()
    {
        // A caller with nowhere to report to — a client that sent no progress token, or speculative work
        // nobody is waiting on — gets nothing to dispose. Answering null here is what leaves every call site
        // with one nullable reporter to await-using rather than a branch around the whole feature.
        JbRunProgress? progress = JbRunProgress.Reporting(
            "inspectcode", SolutionPath, Cap, null, NullLogger.Instance, Brisk);

        progress.ShouldBeNull();
    }

    [Fact]
    public async Task Reporting_ASink_RendersBeatsThroughTheRunProgressFormatter()
    {
        // Arrange — the one place a snapshot becomes prose, shared by every caller that reports itself. What
        // reaches the sink is already the line RunProgressFormatter writes, which is what keeps a run's
        // phases from having to be understood anywhere above here.
        RecordingSink<string> lines = new(Generous);

        // Act — labelled as the cache reset, the caller that has a queue wait and no jb at all.
        await using JbRunProgress? progress = JbRunProgress.Reporting(
            "cache reset", SolutionPath, Cap, lines.Record, NullLogger.Instance, Brisk);

        // Assert
        progress.ShouldNotBeNull();
        await lines.WaitForAsync(1, Ct);
        lines.Items[0].ShouldBe("cache reset on App.slnx: starting");
    }

    private static JbRunProgress Build(Sink sink, TimeSpan? interval = null)
    {
        return new JbRunProgress(
            "inspectcode",
            SolutionPath,
            Cap,
            sink.Report,
            NullLogger.Instance,
            interval ?? Brisk);
    }

    /// <summary>Records every beat, and can be told to be slow or to throw.</summary>
    private sealed class Sink() : RecordingSink<JbRunProgressSnapshot>(Generous)
    {
        /// <summary>When set, every beat throws — a sink that cannot be allowed to end the run.</summary>
        public bool Throw { get; init; }

        /// <summary>When set, every beat blocks for this long, so disposal overlaps one in flight.</summary>
        public TimeSpan Dawdle { get; init; }

        public void Report(JbRunProgressSnapshot snapshot)
        {
            Record(snapshot);

            if (Dawdle > TimeSpan.Zero) Thread.Sleep(Dawdle);

            if (Throw) throw new InvalidOperationException("The client went away mid-run.");
        }

        /// <summary>The first beat that lands after <paramref name="alreadySeen" /> of them have.</summary>
        public async Task<JbRunProgressSnapshot> NextAfterAsync(int alreadySeen, CancellationToken cancellationToken)
        {
            await WaitForAsync(alreadySeen + 1, cancellationToken);

            return Items[alreadySeen];
        }
    }
}