using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     Pins the <see cref="JbRunProgress" /> heartbeat, above all that it stops dead at disposal.
/// </summary>
/// <remarks>
///     A beat that lands after the call it reports has been answered reaches a <c>ProgressSink</c> that has
///     already closed, and is dropped silently; stopping dead at disposal is what keeps the run's last word
///     ahead of the result rather than lost behind it.
/// </remarks>
public sealed class JbRunProgressTests
{
    /// <summary>Short enough that a test sees several beats, long enough not to be flaky under load.</summary>
    private static readonly TimeSpan Brisk = TimeSpan.FromMilliseconds(40);

    /// <summary>Long enough that only a hang reaches it.</summary>
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan Cap = TimeSpan.FromMinutes(10);

    private static readonly string SolutionPath = Path.Combine("C:", "repo", "App.slnx");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void HeartbeatInterval_IsTheTenSecondsTheDocsPromise()
    {
        // The README and the setup guide both say "every ten seconds", so moving the number means changing
        // them too. It carries no protocol meaning, but it is the one fact about this feature a reader can check
        // against a stopwatch.
        JbRunProgress.HeartbeatInterval.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Constructor_BeatsImmediately_RatherThanWaitingOutTheFirstInterval()
    {
        // Arrange — the silence starts the moment the call arrives, so the first beat cannot wait an interval:
        // with the production interval, one that did would leave the caller ten seconds in the dark.
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
    public async Task Beats_TheFirstOneIsSlow_NeverOverlapTheOnesQueuedBehindIt()
    {
        // Arrange — a beat that outlasts its interval, so the timer queues more callbacks while it runs.
        // System.Threading.Timer does not serialize them, and a starved thread pool makes this reachable in the
        // field.
        Sink sink = new() { Dawdle = TimeSpan.FromMilliseconds(150) };

        // Act — several intervals fit inside the first beat.
        await using JbRunProgress progress = Build(sink, TimeSpan.FromMilliseconds(10));
        await sink.WaitForAsync(3, Ct);

        // Assert — the extra callbacks were skipped rather than run beside the beat in flight. Two that did
        // overlap would snapshot in one order and reach the sink in the other, so a message could name an
        // earlier elapsed than the one before it.
        sink.MaxConcurrentBeats.ShouldBe(1);
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
        // Arrange — ProcessRunner can abandon a live reader at the cap, so a line can arrive after the run it
        // describes has thrown. It has to be harmless, not only unlikely.
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

        // Assert — a beat reports the count too, waited for whole: one taken between two of the lines above
        // carries the phase with a count short of it.
        progress.FilesSeen.ShouldBe(2);
        await sink.FirstAsync(
            snapshot => snapshot is { Phase: JbRunPhase.Analyzing, FilesSeen: 2 },
            "a beat in the analysis sweep with 2 files seen",
            Ct);
    }

    [Fact]
    public async Task OnOutputLine_TheSecondSweep_RestartsTheCountRatherThanCarryingIt()
    {
        // Arrange — jb's two sweeps report different totals for the same solution, so a running total would be
        // a number matching nothing jb said.
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
        await sink.FirstAsync(
            snapshot => snapshot is { Phase: JbRunPhase.Inspecting, FilesSeen: 1 },
            "a beat in the inspection sweep with 1 file seen",
            Ct);
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

        // Assert — Spawning sets the phase, the cap and the summary together, so the beat that carries the
        // phase carries the other two.
        JbRunProgressSnapshot afterSpawn = await sink.FirstAsync(
            snapshot => snapshot.Phase == JbRunPhase.Starting, "a beat after the spawn", Ct);
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
        JbRunProgressSnapshot seeding = await sink.FirstAsync(
            snapshot => snapshot.Phase == JbRunPhase.Seeding, "a beat in the seeding phase", Ct);
        seeding.Cap.ShouldBeNull();
    }

    [Fact]
    public void Reporting_NoSink_BuildsNoReporterAtAll()
    {
        // A caller with nowhere to report to — a client that sent no progress token, or speculative work
        // nobody is waiting on — gets nothing to dispose. Answering null here is what leaves every call site
        // with one nullable reporter to await-using rather than a branch around the whole feature.
        JbRunProgress? progress = JbRunProgress.Reporting(
            "inspectcode", SolutionPath, Cap, null, NullLogger.Instance, JbRunPhase.Queued, Brisk);

        progress.ShouldBeNull();
    }

    [Fact]
    public async Task Reporting_ASink_RendersBeatsThroughTheRunProgressFormatter()
    {
        // Arrange — the one place a snapshot becomes prose: what reaches the sink is already the line
        // RunProgressFormatter writes, which is what keeps a run's phases from having to be understood anywhere
        // above here.
        RecordingSink<string> lines = new(Generous);

        // Act — labelled as the cache reset, the caller that has a queue wait and no jb at all.
        await using JbRunProgress? progress = JbRunProgress.Reporting(
            "cache reset", SolutionPath, Cap, lines.Record, NullLogger.Instance, JbRunPhase.Queued, Brisk);

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
            JbRunPhase.Queued,
            interval ?? Brisk);
    }

    /// <summary>Records every beat, counts how many were ever inside at once, and can be told to be slow or to throw.</summary>
    private sealed class Sink() : RecordingSink<JbRunProgressSnapshot>(Generous)
    {
        private readonly Lock _beats = new();
        private int _inside;
        private int _maxInside;

        /// <summary>Gets a value indicating whether every beat throws.</summary>
        public bool Throw { get; init; }

        /// <summary>Gets how long every beat blocks, holding a beat in flight.</summary>
        public TimeSpan Dawdle { get; init; }

        /// <summary>Gets the most beats ever inside this sink at once: one, if they are serialized.</summary>
        public int MaxConcurrentBeats
        {
            get
            {
                lock (_beats)
                {
                    return _maxInside;
                }
            }
        }

        public void Report(JbRunProgressSnapshot snapshot)
        {
            lock (_beats)
            {
                _inside++;
                _maxInside = Math.Max(_maxInside, _inside);
            }

            try
            {
                Record(snapshot);

                if (Dawdle > TimeSpan.Zero) Thread.Sleep(Dawdle);

                if (Throw) throw new InvalidOperationException("The client went away mid-run.");
            }
            finally
            {
                lock (_beats)
                {
                    _inside--;
                }
            }
        }
    }
}