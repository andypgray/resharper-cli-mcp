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
///     Pins what the speculative pre-warm must never do, and that it may run again once a pass has settled,
///     which is the recurrence a call that hit the run cap depends on. Each refusal is an ordinary
///     <see cref="WarmUpOutcome" />, so it is asserted directly rather than read out of log lines.
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
        _solutionPath = _environment.PlantSolution("App.sln");

        _cacheHome = _environment.CreateTempDirectory();
        _environment.SetVariable("JB_CACHE_HOME", _cacheHome);

        _loggerFactory = Logs.Capturing(_logs);
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
    public async Task Start_MarkerStampedRecentlyByThisBuildUnderNamingDrift_SkipsWithoutRunningJb()
    {
        // Arrange — a marker stamped moments ago that names no generation, which is every marker a run
        // writes once jb's directory naming has drifted from what this server computes. It still records the
        // build, so the window holds: without it, every drifted marker would read as another build's, and the
        // pass would analyse the solution at every session start.
        JbWarmMarker.Stamp(_solutionPath, _cacheHome, NullLogger.Instance, JbStubs.Version);
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert
        warmer.Outcome.ShouldBe(WarmUpOutcome.AlreadyWarm);
        _probe.Runs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_MarkerStampedRecentlyByAnotherBuildUnderNamingDrift_WarmsAnyway()
    {
        // Arrange — drift does not exempt a marker from the build check: the pre-update build stamped it, and
        // jb rebuilds what that build wrote whether or not this server can name the directory.
        JbWarmMarker.Stamp(_solutionPath, _cacheHome, NullLogger.Instance, "2026.0.9");
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert
        warmer.Outcome.ShouldBe(WarmUpOutcome.Warmed);
        _probe.Runs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Start_EmptyMarkerFromAnEarlierServerStampedRecently_WarmsOnce()
    {
        // Arrange — what a server from before builds were recorded left behind, drifted or not. It names no
        // build, so it reads as another build's: one pass that may be redundant, after which the marker this
        // pass stamps names the build and the window holds again.
        File.WriteAllText(JbWarmMarker.PathFor(_solutionPath, _cacheHome), string.Empty);
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert
        warmer.Outcome.ShouldBe(WarmUpOutcome.AlreadyWarm);
        _probe.Runs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Start_FreshMarkerWhoseContentCannotBeRead_WarmsAnyway()
    {
        // Arrange — the marker's time reads fresh while its content cannot be read, here because another
        // handle holds it exclusively. An unreadable marker may permit a redundant pass but must never
        // suppress one, so the build it cannot name reads as another build's.
        CacheHomes.PlantWarmDonor(_cacheHome, _solutionPath, JbStubs.Version);
        using CacheWarmer warmer = BuildWarmer();

        // Act
        using (new FileStream(JbWarmMarker.PathFor(_solutionPath, _cacheHome), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            warmer.Start();
            await warmer.Finished.WaitAsync(Generous, Ct);
        }

        // Assert
        warmer.Outcome.ShouldBe(WarmUpOutcome.Warmed);
        _probe.Runs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Start_MarkerNamingACollectedGenerationBesideAnotherOwnedOne_JudgesTheBuildAsTheStateLineDoes()
    {
        // Arrange — jb collected the generation the marker names and another of this solution's remains. The
        // state line judges the remaining one by the marker's build, so the debounce has to as well: a stale
        // line over a skipped pass hands the rebuild to the first call.
        string collected = CacheHomes.PlantWarmDonor(_cacheHome, _solutionPath, "2026.0.9");
        CacheHomes.PlantFork(_cacheHome, collected);
        Directory.Delete(collected, true);
        using CacheWarmer warmer = BuildWarmer();

        // Read before the pass, which restamps the marker.
        JbCacheState state = JbCacheState.Read(_solutionPath, _cacheHome, false, JbStubs.Version, NullLogger.Instance);
        state.Summary.ShouldStartWith("stale");

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert
        warmer.Outcome.ShouldBe(WarmUpOutcome.Warmed);
        _probe.Runs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Start_MarkerStampedRecentlyByThisBuild_SkipsWithoutRunningJb()
    {
        // Arrange — the jb this pass would run warmed this generation moments ago: a call in this session,
        // or another session entirely.
        CacheHomes.PlantWarmDonor(_cacheHome, _solutionPath, JbStubs.Version);
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert
        warmer.Outcome.ShouldBe(WarmUpOutcome.AlreadyWarm);
        _probe.Runs.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("2026.0.9")]
    [InlineData(null)]
    public async Task Start_MarkerStampedRecentlyByAnotherBuild_WarmsAnyway(string? markerJbVersion)
    {
        // Arrange — what a jb update leaves behind: a marker minutes old over a cache another build wrote,
        // which jb rebuilds in place, so a skip would hand the rebuild to the first call. The null row is a
        // marker from before the build was recorded, which reads as another build's for the same reason.
        CacheHomes.PlantWarmDonor(_cacheHome, _solutionPath, markerJbVersion);
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert
        warmer.Outcome.ShouldBe(WarmUpOutcome.Warmed);
        _probe.Runs.Count.ShouldBe(1);
        _logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_JbUpdatedInPlaceUnderALiveServer_WarmsWithTheNewBuild()
    {
        // Arrange — one server across an in-place update, which rewrites the shim and leaves the new build
        // behind it. A first pass warms and stamps the old build.
        string shim = JbInstalls.PlantGlobalToolShim(_environment.HomeDirectory);
        using CacheWarmer warmer = BuildWarmer();
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);
        warmer.Outcome.ShouldBe(WarmUpOutcome.Warmed);

        JbInstalls.UpdateInPlace(shim);
        _probe.Version = "2026.2.0";

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert — both halves are needed. A server that kept its first probe would read its own fresh marker
        // as this build's, and a debounce asking only the age would skip it either way; the pass runs, and
        // stamps the new build.
        warmer.Outcome.ShouldBe(WarmUpOutcome.Warmed);
        _probe.Runs.Count.ShouldBe(2);
        JbWarmMarker.TryReadJbVersion(JbWarmMarker.PathFor(_solutionPath, _cacheHome), NullLogger.Instance).ShouldBe("2026.2.0");
    }

    [Fact]
    public async Task Start_MarkerOlderThanTheWindow_WarmsAnyway()
    {
        // Arrange — a marker this build stamped, so its age alone decides. It is aged against the shipped window
        // itself, which pins the real threshold rather than an injectable stand-in for it.
        JbWarmMarker.Stamp(_solutionPath, _cacheHome, NullLogger.Instance, JbStubs.Version);
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
        // the whole of what Skipped claims: no jb was spawned, and the empty run list is the proof.
        warmer.Outcome.ShouldBe(WarmUpOutcome.Skipped);
        _probe.Runs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_WhileAPassIsInFlight_DoesNotStartASecond()
    {
        // Arrange — a pass held mid-run. A client that re-sends `initialized`, or any second trigger, must not
        // cost a second full solution analysis on top of the one already going.
        _probe.BlockUntilCancelled = true;
        using CacheWarmer warmer = BuildWarmer();
        warmer.Start();
        await _probe.Started.WaitAsync(Generous, Ct);

        // Act
        warmer.Start();

        // Assert — the first pass is still running, and there is still only one.
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

        // Act — re-arming is allowed: a once-per-process latch would switch the pre-warm off when a call that
        // has hit the cap needs it.
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert — both halves in one test: the second pass ran, and it decided for *itself* not to spend a
        // jb. The debounce is what stops the repeat work, not an inability to start.
        warmer.Outcome.ShouldBe(WarmUpOutcome.AlreadyWarm);
        _probe.Runs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Start_AfterStopAsync_NeverRunsAtAll()
    {
        // Arrange — the host has stopped, so a pass starting now would spawn a jb with no drain left to wait
        // for it.
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
        // Arrange — end to end, through the warmer and the runner it is wired to: only a run driven through
        // that runner re-arms that warmer. The subscription only exists once the hosted service has started,
        // which is why this test calls StartAsync.
        WarmerGraph graph = BuildGraph();
        using CacheWarmer warmer = graph.Warmer;
        await warmer.StartAsync(Ct);

        ResolvedConfig config = Configs.Bare(_solutionPath, _cacheHome);
        _probe.Fault = new ProcessTimeoutException("'jb' timed out.");
        _probe.FaultUntilRun = 1;

        // Act — a call the user made, killed at the cap.
        await Should.ThrowAsync<UserErrorException>(() => graph.Runner.RunAsync(config, ["inspectcode", _solutionPath], Ct));

        // Assert
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
        // one is a fresh race rather than a re-run against a closed door.
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

        // Assert — a failed background run is an outcome, not an incident: the session pays the cold cost it
        // would have paid anyway, and warnings stay for the unexpected.
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
        // Arrange — a pass held mid-analysis. Only a foreground call through the runner the warmer is wired
        // to reaches that pass's claim, which is why the graph hands the runner back.
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
        // client arrived. Waiting out the drain here would add its whole timeout to every such shutdown, so
        // the bound below is shorter than that timeout.
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
        // with the start breaks where the reader most needs it: a pass that did nothing. A server in a
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
        // Arrange
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

        // Assert — every line the pass caused, its start and outcome lines among them, carries the same id.
        IReadOnlyList<LogEntry> mine = _logs.Entries
            .Where(entry => entry.ScopeValue(RunIdScope.PropertyName) is not null)
            .ToList();

        mine.Count.ShouldBeGreaterThanOrEqualTo(2);
        mine.Select(entry => entry.ScopeValue(RunIdScope.PropertyName)).Distinct().ShouldHaveSingleItem();
    }

    /// <summary>Returns the one line saying how the pass settled.</summary>
    private LogEntry OutcomeLine()
    {
        return _logs.WithProperty("Outcome").ShouldHaveSingleItem();
    }

    private CacheWarmer BuildWarmer()
    {
        return BuildGraph().Warmer;
    }

    /// <summary>
    ///     Builds the tool graph as far as <see cref="InspectService" />, topped with the warmer, so these tests
    ///     drive the real discovery, lock and process path rather than a stubbed one.
    /// </summary>
    /// <remarks>
    ///     The lock is private to the returned warmer, so a test that needs the cache generation held takes the
    ///     lock file instead.
    /// </remarks>
    private WarmerGraph BuildGraph()
    {
        JbLocator jbLocator = new(_probe, _environment, NullLogger<JbLocator>.Instance);
        ConfigResolver configResolver = new(jbLocator, _environment, NullLogger<ConfigResolver>.Instance);
        JbRunner jbRunner = JbRunners.Create(_probe);
        InspectService inspectService = new(jbRunner);
        ILogger<CacheWarmer> logger = _loggerFactory.CreateLogger<CacheWarmer>();
        CacheWarmer warmer = new(configResolver, inspectService, jbRunner, _environment, logger);
        return new WarmerGraph(warmer, jbRunner);
    }

    /// <summary>
    ///     Holds a warmer and the runner underneath it, handed back together because the two are wired to each
    ///     other.
    /// </summary>
    private sealed record WarmerGraph(CacheWarmer Warmer, JbRunner Runner);

    /// <summary>
    ///     Fakes a scriptable <c>jb</c> that answers the version probe, so discovery succeeds, and records every
    ///     real run.
    /// </summary>
    private sealed class JbProbe : IProcessRunner
    {
        private readonly List<IReadOnlyList<string>> _runs = [];
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        /// <summary>Gets or sets the exit code every non-probe run reports.</summary>
        public int ExitCode { get; set; }

        /// <summary>Gets or sets the build the version probe reports; change it to stand in for an update.</summary>
        public string Version { get; set; } = JbStubs.Version;

        /// <summary>
        ///     Gets or sets a value indicating whether every <c>jb</c> candidate fails its version probe, so the
        ///     toolchain reads as absent.
        /// </summary>
        public bool JbMissing { get; set; }

        /// <summary>Gets or sets an exception every non-probe run throws instead of running.</summary>
        public Exception? Fault { get; set; }

        /// <summary>Gets or sets the last run number <see cref="Fault" /> applies to, so a later run can succeed.</summary>
        public int FaultUntilRun { get; set; } = int.MaxValue;

        /// <summary>
        ///     Gets or sets a value indicating whether every non-probe run parks until its own token is cancelled,
        ///     so a test observes a pass mid-run rather than racing it with a sleep.
        /// </summary>
        public bool BlockUntilCancelled { get; set; }

        /// <summary>Gets a task that completes as soon as a non-probe run has started.</summary>
        public Task Started => _started.Task;

        /// <summary>Gets the number of processes this fake was asked to start, version probes included.</summary>
        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Gets the argument lists of every run except the version probes.</summary>
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
            CancellationToken cancellationToken,
            Action<string>? onOutputLine = null)
        {
            Interlocked.Increment(ref _calls);

            if (JbStubs.IsVersionProbe(arguments))
                return JbMissing
                    ? new ProcessResult(1, string.Empty, "jb: command not found")
                    : JbStubs.VersionProbeAnswerFor(Version);

            int runNumber;
            lock (_runs)
            {
                _runs.Add(arguments);
                runNumber = _runs.Count;
            }

            _started.TrySetResult();

            if (Fault is not null && runNumber <= FaultUntilRun) throw Fault;

            if (BlockUntilCancelled) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            JbStubs.WriteEmptySarifIfRequested(arguments);

            return ExitCode == 0
                ? JbStubs.Succeed(arguments)
                : new ProcessResult(ExitCode, string.Empty, string.Empty);
        }
    }
}