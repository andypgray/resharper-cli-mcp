using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Infrastructure;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Services;

/// <summary>
///     The pre-warm is speculative work, so the invariant under test is what it must <em>never</em> do:
///     never run when it was turned off, never run when there is nothing to warm or the cache generation is
///     already warm or already busy, never run a second pass alongside one already in flight, never start
///     one after shutdown, never raise anything through the log that is not a genuine surprise, and never
///     leave a <c>jb</c> behind when the server stops. Every one of those is an ordinary
///     <see cref="WarmUpOutcome" />, which is why they can be asserted directly instead of sniffed out of
///     log lines. What it <em>may</em> do is run again once a pass has settled — the recurrence a call that
///     hit the run cap depends on.
/// </summary>
public sealed class CacheWarmerTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private readonly string _cacheHome;
    private readonly FakeEnvironment _environment = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly CapturingLoggerProvider _logs = new();
    private readonly JbProbe _probe = new();
    private readonly string _solutionPath;

    public CacheWarmerTests()
    {
        _solutionPath = Path.Combine(_environment.CurrentDirectory, "App.sln");
        File.WriteAllText(_solutionPath, string.Empty);

        _cacheHome = _environment.CreateTempDirectory();
        _environment.SetVariable("JB_CACHE_HOME", _cacheHome);

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(_logs);
            builder.SetMinimumLevel(LogLevel.Trace);
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _loggerFactory.Dispose();
        _environment.Dispose();
    }

    [Fact]
    public async Task Start_ColdCache_RunsAFullSolutionInspectionAndRecordsItAsWarm()
    {
        // Arrange
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert — a full solution run, because --include does not shrink jb's work, so there is no cheaper
        // shape a warm-up could take.
        warmer.Outcome.ShouldBe(WarmUpOutcome.Warmed);
        IReadOnlyList<string> arguments = _probe.Runs.ShouldHaveSingleItem();
        arguments[0].ShouldBe("inspectcode");
        arguments.ShouldContain(_solutionPath);
        arguments.Any(argument => argument.StartsWith("--include", StringComparison.Ordinal)).ShouldBeFalse();

        // ...and the debounce closes the loop, so the next session start finds this generation warm.
        JbWarmMarker.IsFreshWithin(_solutionPath, _cacheHome, CacheWarmer.RecentlyWarmWindow, NullLogger.Instance).ShouldBeTrue();
        _logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_TurnedOff_DoesNotSoMuchAsLookForJb()
    {
        // Arrange — the kill switch has to be the very first thing checked, or turning it off would still
        // cost a process spawn at every session start.
        _environment.SetVariable(CacheWarmer.EnableVariable, "off");
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert
        warmer.Outcome.ShouldBe(WarmUpOutcome.Disabled);
        _probe.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Start_NoSolutionToDiscover_ReportsNoTargetSilently()
    {
        // Arrange — a server started in a directory holding no solution, which is the ordinary case for a
        // user-scope server in a repo that is not a .NET one. Nothing to warm, and nobody to tell.
        _environment.CurrentDirectory = _environment.CreateTempDirectory();
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert
        warmer.Outcome.ShouldBe(WarmUpOutcome.NoTarget);
        _probe.Runs.ShouldBeEmpty();
        _logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_JbNotInstalled_ReportsNoTargetSilently()
    {
        // Arrange
        _probe.JbMissing = true;
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert — an uninstalled toolchain is the user's business when they call a tool, not a background
        // task's business to complain about.
        warmer.Outcome.ShouldBe(WarmUpOutcome.NoTarget);
        _probe.Runs.ShouldBeEmpty();
        _logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_MarkerStampedRecently_SkipsWithoutRunningJb()
    {
        // Arrange — something warmed this generation moments ago: a call in this session, or another
        // session entirely.
        JbWarmMarker.Stamp(_solutionPath, _cacheHome, NullLogger.Instance);
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert
        warmer.Outcome.ShouldBe(WarmUpOutcome.AlreadyWarm);
        _probe.Runs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_MarkerOlderThanTheWindow_WarmsAnyway()
    {
        // Arrange — aged against the shipped window itself, so the real threshold is pinned rather than an
        // injectable stand-in for it.
        JbWarmMarker.Stamp(_solutionPath, _cacheHome, NullLogger.Instance);
        File.SetLastWriteTimeUtc(
            JbWarmMarker.PathFor(_solutionPath, _cacheHome),
            DateTime.UtcNow - CacheWarmer.RecentlyWarmWindow - TimeSpan.FromMinutes(1));
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert
        warmer.Outcome.ShouldBe(WarmUpOutcome.Warmed);
        _probe.Runs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Start_CacheGenerationHeldByAnotherProcess_SkipsWithoutRunningJb()
    {
        // Arrange — running while another process holds the generation would fork a cold cache generation,
        // which is the whole failure the run lock exists to prevent.
        await using FileStream otherProcess = CacheHomes.HoldLockFile(_cacheHome, _solutionPath);
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert — and it gave up rather than queueing, which is what the bounded wait above proves. This is
        // now the whole of what Skipped claims: no jb was spawned, and the empty run list is the proof.
        warmer.Outcome.ShouldBe(WarmUpOutcome.Skipped);
        _probe.Runs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_WhileAPassIsInFlight_DoesNotStartASecond()
    {
        // Arrange — a pass held mid-run, so this is an observation rather than a race with a sleep. A client
        // that re-sends `initialized`, or any second trigger, must not cost a second full solution analysis
        // on top of the one already going.
        _probe.BlockUntilCancelled = true;
        using CacheWarmer warmer = BuildWarmer();
        warmer.Start();
        await _probe.Started.WaitAsync(Generous, Ct);

        // Act
        warmer.Start();

        // Assert — the first pass is provably still running, and there is still only one.
        _probe.Runs.Count.ShouldBe(1);

        await warmer.StopAsync(Ct).WaitAsync(Generous, Ct);
    }

    [Fact]
    public async Task Start_AfterAPassHasSettled_IsAllowedButTheDebounceStillGovernsIt()
    {
        // Arrange — one pass, run to completion, which stamps the warm marker.
        using CacheWarmer warmer = BuildWarmer();
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);
        warmer.Outcome.ShouldBe(WarmUpOutcome.Warmed);

        // Act — re-arming is allowed now. The one-shot latch this replaced forbade it for the life of the
        // process, which switched the pre-warm off exactly when a call that had hit the cap needed it.
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert — both halves in one test: the second pass really ran, and it decided for *itself* not to
        // spend a jb. The debounce is what stops the repeat work now, not an inability to start.
        warmer.Outcome.ShouldBe(WarmUpOutcome.AlreadyWarm);
        _probe.Runs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Start_AfterStopAsync_NeverRunsAtAll()
    {
        // Arrange — the host has stopped. A pass starting now would outlive the process holding ReSharper's
        // own cache-generation lock, which outlives our lock file and is the one orphan the run lock cannot
        // protect the next session from.
        using CacheWarmer warmer = BuildWarmer();
        await warmer.StopAsync(Ct).WaitAsync(Generous, Ct);

        // Act
        warmer.Start();

        // Assert — a negative, so it is given time to be wrong in. Calls rather than Runs: a pass that got
        // as far as looking for jb has already started, whatever it decided afterwards.
        await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        _probe.Calls.ShouldBe(0);
        warmer.Outcome.ShouldBe(WarmUpOutcome.NotRun);
    }

    [Fact]
    public async Task ForegroundRunHittingTheCap_ReArmsAPassForTheSolutionThatTimedOut()
    {
        // Arrange — the change end to end, through the two objects that have to agree. The subscription only
        // exists once the hosted service has started, which is why this is the one test here that calls
        // StartAsync. The cap is exactly the moment the pre-warm used to be guaranteed never to run again.
        WarmerGraph graph = BuildGraph();
        using CacheWarmer warmer = graph.Warmer;
        await warmer.StartAsync(Ct);

        ResolvedConfig config = Configs.Bare(_solutionPath, _cacheHome);
        _probe.Fault = new ProcessTimeoutException("'jb' timed out.");
        _probe.FaultUntilRun = 1;

        // Act — a call the user made, killed at the cap.
        await Should.ThrowAsync<UserErrorException>(() => graph.Runner.RunAsync(config, ["inspectcode", _solutionPath], Ct));

        // Assert — a pass ran, and it warmed the solution that actually timed out.
        await warmer.Finished.WaitAsync(Generous, Ct);
        warmer.Outcome.ShouldBe(WarmUpOutcome.Warmed);
        _probe.Runs.Count.ShouldBe(2);
        _probe.Runs[^1].ShouldContain(_solutionPath);

        // ...and it paid no discovery to find it: two runs and no third process means no version probe, so
        // the config rode the signal rather than being resolved again.
        _probe.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Start_RacingStopAsync_NeverStartsAPassAfterTheDrainHasReturned()
    {
        // Arrange — the interleaving the lock exists for, repeated enough to catch it. Unguarded, Start can
        // claim the slot while StopAsync reads the *previous* pass's already-settled task, drains instantly
        // and returns, leaving Start to spawn a jb after shutdown. Each attempt gets its own warmer, so each
        // one really is a fresh race rather than a re-run against a closed door.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using CacheWarmer warmer = BuildWarmer();

            // Act — both awaited below before the using disposes, so the capture outlives nothing.
            // ReSharper disable once AccessToDisposedClosure
            Task starting = Task.Run(() => warmer.Start(), Ct);
            Task stopping = warmer.StopAsync(Ct);
            await Task.WhenAll(starting, stopping).WaitAsync(Generous, Ct);

            // Assert — whatever the race decided, nothing may start once the drain has returned.
            int callsAtShutdown = _probe.Calls;
            await Task.Delay(TimeSpan.FromMilliseconds(5), Ct);
            _probe.Calls.ShouldBe(callsAtShutdown, $"a pass started after shutdown on attempt {attempt}");
        }
    }

    [Fact]
    public async Task Start_JbExitsNonZero_ReportsFailedWithoutStampingOrWarning()
    {
        // Arrange — a jb that failed warmed nothing, so the next session must still try.
        _probe.ExitCode = 3;
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert — a failed background run is an outcome, not an incident: the session simply pays the cold
        // cost it would have paid anyway, and the log stays for genuine surprises.
        warmer.Outcome.ShouldBe(WarmUpOutcome.Failed);
        JbWarmMarker.IsFreshWithin(_solutionPath, _cacheHome, CacheWarmer.RecentlyWarmWindow, NullLogger.Instance).ShouldBeFalse();
        _logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_JbKilledAtTheRunCap_ReportsCappedWithoutStampingOrWarning()
    {
        // Arrange — on a large cold solution this is the *normal* shape of a pass, not an incident: the cap
        // exists to stop a call the user is waiting on hanging, and nobody is waiting on this one.
        _probe.Fault = new ProcessTimeoutException("'jb' timed out.");
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert — its own word, distinct from both a failure and a skip. The run line above it says the cap
        // killed a jb after N ms, and a summary reading "Skipped" beside that is a contradiction; nothing is
        // stamped either way, so the next call still finds this generation unvouched-for.
        warmer.Outcome.ShouldBe(WarmUpOutcome.Capped);
        JbWarmMarker.IsFreshWithin(_solutionPath, _cacheHome, CacheWarmer.RecentlyWarmWindow, NullLogger.Instance).ShouldBeFalse();
        _logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_AToolCallReclaimingTheGeneration_ReportsCancelledRatherThanSkipped()
    {
        // Arrange — a pass provably mid-analysis, so this is an observation rather than a race with a sleep.
        // The graph hands back the runner the warmer is wired to, which is the only way a foreground call in
        // a test reaches that pass's claim at all.
        _probe.BlockUntilCancelled = true;
        WarmerGraph graph = BuildGraph();
        using CacheWarmer warmer = graph.Warmer;
        warmer.Start();
        await _probe.Started.WaitAsync(Generous, Ct);

        // Act — a call the user made, which takes the cache generation back on entry.
        using CancellationTokenSource callerGivesUp = new();
        Task<ProcessResult> foreground = graph.Runner.RunAsync(
            Configs.Bare(_solutionPath, _cacheHome), ["inspectcode", _solutionPath], callerGivesUp.Token);
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert — a running jb was stopped from outside, which is what Cancelled means; the shutdown path
        // reaches the same word for the same reason. Reporting a skip here would deny minutes of analysis
        // that the run line above it has already recorded as cancelled.
        warmer.Outcome.ShouldBe(WarmUpOutcome.Cancelled);

        // The foreground call parks on the probe in its turn, so it is cancelled rather than left running.
        await callerGivesUp.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => foreground.WaitAsync(Generous, Ct));
    }

    [Fact]
    public async Task Start_UnexpectedFailure_SettlesAndLogsExactlyOneWarning()
    {
        // Arrange — nothing a pre-warm can foresee, so this one *is* log-worthy.
        _probe.Fault = new InvalidOperationException("something nobody predicted");
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert — it still settles: a fire-and-forget task that faulted silently would leave Finished
        // hanging and take host shutdown's drain with it.
        warmer.Outcome.ShouldBe(WarmUpOutcome.Failed);
        _logs.Warnings.ShouldHaveSingleItem().Exception.ShouldBe(_probe.Fault);
    }

    [Fact]
    public async Task StopAsync_WithARunInFlight_CancelsItAndReturnsWithinTheDrain()
    {
        // Arrange — a jb mid-analysis when the client disconnects. Cancelling without waiting would leave it
        // alive holding ReSharper's own cache-generation lock, which outlives our lock file and so is the one
        // orphan the run lock cannot protect the next session from.
        _probe.BlockUntilCancelled = true;
        using CacheWarmer warmer = BuildWarmer();
        warmer.Start();
        await _probe.Started.WaitAsync(Generous, Ct);

        // Act
        await warmer.StopAsync(Ct).WaitAsync(Generous, Ct);

        // Assert
        warmer.Outcome.ShouldBe(WarmUpOutcome.Cancelled);
        warmer.Finished.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task StopAsync_WithNothingEverStarted_ReturnsImmediately()
    {
        // Arrange — a session that connected but never sent `initialized`, or a host stopped before any
        // client arrived. Waiting out the drain here would add ten seconds to every such shutdown.
        using CacheWarmer warmer = BuildWarmer();

        // Act
        await warmer.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // Assert
        warmer.Outcome.ShouldBe(WarmUpOutcome.NotRun);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("on", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("something unrecognised", true)]
    [InlineData("off", false)]
    [InlineData("OFF", false)]
    [InlineData("  off  ", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("disabled", false)]
    public void IsEnabled_TreatsTheDocumentedSpellingsAsOffAndEverythingElseAsOn(string? value, bool expected)
    {
        // Assert — unrecognised falls back to the shipped default, in the same register as the log-level
        // variable. `off` is the spelling the docs teach; the rest are what a user might reasonably try.
        CacheWarmer.IsEnabled(value).ShouldBe(expected);
    }

    /// <summary>
    ///     The pre-warm records how it ended, at <see cref="LogLevel.Information" />, pairing with the
    ///     <c>Information</c> start it already wrote.
    /// </summary>
    /// <remarks>
    ///     A week of field logs showed pre-warms beginning and never ending, because the outcome was a
    ///     <c>LogDebug</c> and the deployment runs at <c>Information</c>. Two passes from two sessions started
    ///     an hour apart and whether they contended, and which of them won, was unanswerable. Both halves of
    ///     the pair have to be at the same level or the pair is not one.
    /// </remarks>
    [Fact]
    public async Task Start_APassThatRanJb_RecordsTheOutcomeAtInformationWithItsTargetAndDuration()
    {
        // Arrange
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert
        LogEntry finished = OutcomeLine();
        finished.Level.ShouldBe(LogLevel.Information);
        finished.Property("Outcome").ShouldBe(WarmUpOutcome.Warmed);
        finished.Property("SolutionPath").ShouldBe(_solutionPath);
        finished.Property("ElapsedMs").ShouldNotBeNull();
    }

    [Fact]
    public async Task Start_APassThatFoundNothingToWarm_StillRecordsItsOutcomeAtInformation()
    {
        // Arrange — the line has to be written on every path, not only the interesting one, or the pairing
        // with the start breaks exactly where the reader most needs it: a pass that did nothing. A server in a
        // repo that is not a .NET one lands here, and nothing else in the log would say so.
        _environment.CurrentDirectory = _environment.CreateTempDirectory();
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert
        LogEntry finished = OutcomeLine();
        finished.Level.ShouldBe(LogLevel.Information);
        finished.Property("Outcome").ShouldBe(WarmUpOutcome.NoTarget);
        finished.Property("SolutionPath").ShouldBe("no target");
    }

    [Fact]
    public async Task Start_TurnedOff_KeepsItsOutcomeAtDebugBecauseTheStartupLineAlreadySaysSo()
    {
        // Arrange — the one outcome that is not worth an Information line: the switch's position is in the
        // startup fingerprint already, and restating it once per session is the noise that level was cleared
        // out to make room for real events.
        _environment.SetVariable(CacheWarmer.EnableVariable, "off");
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert
        LogEntry finished = OutcomeLine();
        finished.Level.ShouldBe(LogLevel.Debug);
        finished.Property("Outcome").ShouldBe(WarmUpOutcome.Disabled);
    }

    [Fact]
    public async Task Start_APassThatRan_TagsEveryLineItCausedWithOneRunId()
    {
        // Arrange — a pass overlaps a tool call by design, and in one shared log file their cache-state, queue
        // wait and run lines interleave with nothing to tell them apart. The scope is what separates them.
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert — the start and the outcome are the two lines this class writes, and both carry it.
        IReadOnlyList<LogEntry> mine = _logs.Entries
            .Where(entry => entry.ScopeValue(RunIdScope.PropertyName) is not null)
            .ToList();

        mine.Count.ShouldBeGreaterThanOrEqualTo(2);
        mine.Select(entry => entry.ScopeValue(RunIdScope.PropertyName)).Distinct().ShouldHaveSingleItem();
    }

    /// <summary>The one line saying how the pass settled.</summary>
    private LogEntry OutcomeLine()
    {
        return _logs.WithProperty("Outcome").ShouldHaveSingleItem();
    }

    private CacheWarmer BuildWarmer()
    {
        return BuildGraph().Warmer;
    }

    private WarmerGraph BuildGraph()
    {
        return ToolHarness.BuildCacheWarmer(_probe, _environment, _loggerFactory.CreateLogger<CacheWarmer>());
    }

    /// <summary>
    ///     A scriptable <c>jb</c>: it answers the version probe so discovery succeeds, records every real run,
    ///     and can be told to report a missing toolchain, a non-zero exit, an unforeseen fault, or a run that
    ///     never finishes on its own.
    /// </summary>
    private sealed class JbProbe : IProcessRunner
    {
        private readonly List<IReadOnlyList<string>> _runs = [];
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        /// <summary>Exit code every non-probe run reports.</summary>
        public int ExitCode { get; set; }

        /// <summary>When set, both jb candidates fail their version probe and the toolchain reads as absent.</summary>
        public bool JbMissing { get; set; }

        /// <summary>When set, every non-probe run throws this instead of running.</summary>
        public Exception? Fault { get; set; }

        /// <summary>Caps <see cref="Fault" /> to the first N runs, so a later one can succeed.</summary>
        public int FaultUntilRun { get; set; } = int.MaxValue;

        /// <summary>When set, every non-probe run parks until its own token is cancelled.</summary>
        public bool BlockUntilCancelled { get; set; }

        /// <summary>Completes as soon as a non-probe run has started.</summary>
        public Task Started => _started.Task;

        /// <summary>Every process this fake was asked to start, version probes included.</summary>
        public int Calls => Volatile.Read(ref _calls);

        /// <summary>The argument lists of the real runs, version probes excluded.</summary>
        public IReadOnlyList<IReadOnlyList<string>> Runs
        {
            get
            {
                lock (_runs)
                {
                    return _runs.ToList();
                }
            }
        }

        public async Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);

            if (arguments.Contains("--version"))
                return JbMissing
                    ? new ProcessResult(1, string.Empty, "jb: command not found")
                    : new ProcessResult(0, "Version: 2026.1.2", string.Empty);

            int runNumber;
            lock (_runs)
            {
                _runs.Add(arguments);
                runNumber = _runs.Count;
            }

            _started.TrySetResult();

            if (Fault is not null && runNumber <= FaultUntilRun) throw Fault;

            if (BlockUntilCancelled) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            WriteEmptySarifIfRequested(arguments);
            return new ProcessResult(ExitCode, string.Empty, string.Empty);
        }

        /// <summary>inspectcode is asked for a SARIF file; the warm-up discards it, but jb would still write it.</summary>
        private static void WriteEmptySarifIfRequested(IReadOnlyList<string> arguments)
        {
            string? outputArgument = arguments.FirstOrDefault(argument => argument.StartsWith("-o=", StringComparison.Ordinal));
            if (outputArgument is null) return;

            File.WriteAllText(outputArgument["-o=".Length..], """{"runs":[{"results":[]}]}""");
        }
    }
}