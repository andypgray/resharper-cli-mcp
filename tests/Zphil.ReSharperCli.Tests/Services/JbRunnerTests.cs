using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Services;

/// <summary>
///     What <see cref="JbRunner" /> owns on behalf of both services: naming the failed subcommand in the
///     error, bounding how much of a failed run's standard error comes back with it, and stamping the warm
///     marker on every run that succeeds — and, when that stamp can name no generation, saying so once for
///     the session it belongs to. It is also where a run's duration is recorded under the cache band it
///     started in, and where the band read before <c>jb</c> ran is what a timeout message quotes. Also the
///     shape of the speculative entry point — it skips instead
///     of queueing, and reports instead of throwing — which is what lets background work never affect a
///     call the user made, and the ending it names for each of those, since a caller that cannot tell a run
///     given up after minutes from one that never started will say the wrong one out loud.
/// </summary>
public sealed class JbRunnerTests : IDisposable
{
    private static readonly TimeSpan RecentlyEnough = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private readonly ResolvedConfig _config;
    private readonly FakeEnvironment _environment = new();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly JbRunLock _runLock = JbRunners.Lock();
    private readonly JbRunner _runner;

    public JbRunnerTests()
    {
        _config = Configs.Bare("/sln/App.sln", _environment.CreateTempDirectory());
        _runner = JbRunners.Create(_processRunner, _runLock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_NamesTheSubcommandItWasGiven()
    {
        // Arrange — the subcommand comes from the argument list rather than a parameter, so it cannot drift
        // from what jb was actually asked to do.
        StubExit(3, "profile not found");

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["cleanupcode", _config.SolutionPath], Ct));

        // Assert
        exception.Message.ShouldStartWith("jb cleanupcode exited with code 3.");
        exception.Message.ShouldContain("profile not found");
    }

    [Fact]
    public async Task RunAsync_VeryLongStandardError_KeepsOnlyTheTail()
    {
        // Arrange — jb can emit megabytes on a bad run; only the end of it diagnoses anything, and the
        // response budget is not the place to find that out.
        string noise = new('x', 5000);
        StubExit(1, noise + "the actual failure");

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert
        exception.Message.ShouldContain("the actual failure");
        exception.Message.Length.ShouldBeLessThan(2500);
    }

    [Fact]
    public void StandardErrorTail_NoStandardErrorAtAll_IsEmptyRatherThanThrowing()
    {
        // Arrange & Act — a defaulted ProcessResult carries a null standard error, and the paths that quote
        // a tail (a non-zero exit, a missing SARIF file) are already reporting a failure. Adding a
        // NullReferenceException on top of one would replace the diagnosis with a crash.
        string tail = JbRunner.StandardErrorTail(null);

        // Assert
        tail.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_SucceedingRun_StampsTheWarmMarker()
    {
        // Arrange — a real call warms the cache generation just as thoroughly as a pre-warm does, so it has
        // to refresh the debounce too, or every session start would re-analyse a solution just worked on.
        StubExit(0, string.Empty);

        // Act
        await _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        JbWarmMarker.IsFreshWithin(_config.SolutionPath, _config.CacheHome, RecentlyEnough, NullLogger.Instance).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_FailingRun_LeavesTheWarmMarkerUnstamped()
    {
        // Arrange — a jb that exited non-zero warmed nothing worth skipping a pre-warm over.
        StubExit(2, "boom");

        // Act
        await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert
        JbWarmMarker.IsFreshWithin(_config.SolutionPath, _config.CacheHome, RecentlyEnough, NullLogger.Instance).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_SucceedingRunLeavingNoRecognisableGeneration_WarnsThatNamingHasDrifted()
    {
        // Arrange — a jb that exits zero and leaves no directory carrying this solution's computed hash. On a
        // real machine that means jb's cache-generation naming has moved away from what this server
        // reproduces, and nothing else in the process would ever report it.
        CapturingLoggerProvider logs = new();
        JbRunner runner = JbRunners.Create(_processRunner, _runLock, logs: Logs.Capturing(logs));
        StubExit(0, string.Empty);

        // Act
        await runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert — the two names an operator needs to check the derivation by hand, plus the one phrase that
        // says which derivation broke. Pinned here because this is the wording's only owner: the marker
        // reports the condition and says nothing about it.
        LogEntry drift = logs.Warnings.ShouldHaveSingleItem();
        drift.Property("SolutionPath").ShouldBe(_config.SolutionPath);
        drift.Property("CacheHome").ShouldBe(_config.CacheHome);
        drift.Message.ShouldContain("matching its computed hash");
    }

    [Fact]
    public async Task RunAsync_TwoSucceedingRunsBothLeavingNoRecognisableGeneration_WarnsOnlyOnce()
    {
        // Arrange — the naming is one fact about this machine's jb rather than one fact per run, so a session
        // that repeated it would fill its log with the same sentence for as long as the drift lasted.
        CapturingLoggerProvider logs = new();
        JbRunner runner = JbRunners.Create(_processRunner, _runLock, logs: Logs.Capturing(logs));
        StubExit(0, string.Empty);

        // Act
        await runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);
        await runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        logs.Warnings.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task RunAsync_TwoServerSessionsBothSeeingDrift_EachWarnsInItsOwnLog()
    {
        // Arrange — two runners is two server sessions, which is what a parallel test run puts inside one
        // process. Each has a log of its own, and the latch is per instance for exactly this reason: held on
        // a static, the first session to warn swallows the second's, and the second's log then denies a drift
        // its own run had just met.
        CapturingLoggerProvider firstLogs = new();
        CapturingLoggerProvider secondLogs = new();
        JbRunner firstSession = JbRunners.Create(_processRunner, _runLock, logs: Logs.Capturing(firstLogs));
        JbRunner secondSession = JbRunners.Create(_processRunner, _runLock, logs: Logs.Capturing(secondLogs));
        StubExit(0, string.Empty);

        // Act
        await firstSession.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);
        await secondSession.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        firstLogs.Warnings.ShouldHaveSingleItem();
        secondLogs.Warnings.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task TryRunAsync_SucceedingRunLeavingNoRecognisableGeneration_WarnsThatNamingHasDrifted()
    {
        // Arrange — the pre-warm stamps through the same spawn, so on a session that starts by warming it is
        // the run that meets the drift first. Warning only from the foreground path would leave that session
        // silent about the thing that has just switched half its cache features off.
        CapturingLoggerProvider logs = new();
        JbRunner runner = JbRunners.Create(_processRunner, _runLock, logs: Logs.Capturing(logs));
        StubExit(0, string.Empty);

        // Act
        SpeculativeRunOutcome result = await runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert — speculative work reports rather than throws, and this is the one thing it may still say out
        // loud: the condition is about jb rather than about the pass that happened to notice it.
        result.ShouldBe(SpeculativeRunOutcome.Completed);
        logs.Warnings.ShouldHaveSingleItem().Message.ShouldContain("matching its computed hash");
    }

    [Fact]
    public async Task RunAsync_SucceedingRunLeavingItsGeneration_WarnsNothing()
    {
        // Arrange — the ordinary successful run, which leaves a generation directory carrying this solution's
        // computed hash. A warning that cannot stay quiet says nothing when it fires.
        CapturingLoggerProvider logs = new();
        JbRunner runner = JbRunners.Create(_processRunner, _runLock, logs: Logs.Capturing(logs));
        StubExit(0, string.Empty, () => CacheHomes.PlantGenerationFor(_config.CacheHome, _config.SolutionPath));

        // Act
        await runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_SucceedingRun_DischargesAPrecedingCacheReset()
    {
        // Arrange — a reset promises the next run is cold, and this is that run: the cache it just built is
        // this solution's own, so the promise is kept and there is nothing left to hold anything back from.
        JbColdTombstone.Write(_config.SolutionPath, _config.CacheHome, NullLogger.Instance);
        StubExit(0, string.Empty);

        // Act
        await _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        JbColdTombstone.Exists(_config.SolutionPath, _config.CacheHome, NullLogger.Instance).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_FailingRun_LeavesTheCacheResetUndischarged()
    {
        // Arrange — a jb that exited non-zero may have built nothing, so the reset's promise still stands and
        // the next attempt must not be allowed to shortcut it.
        JbColdTombstone.Write(_config.SolutionPath, _config.CacheHome, NullLogger.Instance);
        StubExit(2, "boom");

        // Act
        await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert
        JbColdTombstone.Exists(_config.SolutionPath, _config.CacheHome, NullLogger.Instance).ShouldBeTrue();
    }

    [Fact]
    public async Task TryRunAsync_CacheGenerationFree_RunsAndStampsTheWarmMarker()
    {
        // Arrange
        StubExit(0, string.Empty);

        // Act
        SpeculativeRunOutcome result = await _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        result.ShouldBe(SpeculativeRunOutcome.Completed);
        JbWarmMarker.IsFreshWithin(_config.SolutionPath, _config.CacheHome, RecentlyEnough, NullLogger.Instance).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_SucceedingRun_RecordsWhatItCostUnderTheBandItStartedIn()
    {
        // Arrange — a cold solution whose run leaves a generation behind, which is the ordinary first run. The
        // band is read before jb starts, so what gets recorded is what a cold run costs — not what the warm
        // cache this run has just produced would.
        StubExit(0, string.Empty, () => CacheHomes.PlantGenerationFor(_config.CacheHome, _config.SolutionPath));

        // Act
        await _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        Recorded(JbCostBand.Cold).ShouldNotBeNull();
        Recorded(JbCostBand.Warm).ShouldBeNull();
    }

    [Fact]
    public async Task RunAsync_RunFiredByATransplant_RecordsItAsSeededRatherThanCold()
    {
        // Arrange — the band that most needs keeping apart. Measured on one solution, a seeded run took 456
        // seconds and the warm run after it 39, so a seeded figure filed under cold would quote seven minutes
        // at a caller about to wait forty seconds — and cold at the moment of the read is exactly what a
        // solution about to be seeded looks like.
        CacheHomes.PlantWarmDonor(_config.CacheHome, SiblingSolutionPath());
        StubExit(0, string.Empty);

        // Act
        await _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        Recorded(JbCostBand.Seeded).ShouldNotBeNull();
        Recorded(JbCostBand.Cold).ShouldBeNull();
    }

    [Fact]
    public async Task TryRunAsync_SucceedingRun_RecordsWhatItCostToo()
    {
        // Arrange — a pre-warm's cold run is a comparable cold run, and on a session that starts by warming
        // it is the only measurement of one there will be.
        StubExit(0, string.Empty);

        // Act
        SpeculativeRunOutcome result = await _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        result.ShouldBe(SpeculativeRunOutcome.Completed);
        Recorded(JbCostBand.Cold).ShouldNotBeNull();
    }

    [Fact]
    public async Task RunAsync_RunHitsTheCap_RecordsNoCostAtAll()
    {
        // Arrange — a run killed at the cap did not finish, so its duration is the cap rather than the
        // solution's. Recording it would teach the next caller that this solution takes exactly as long as
        // whatever budget it was given.
        StubTimeout();

        // Act
        await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert
        Recorded(JbCostBand.Cold).ShouldBeNull();
    }

    [Fact]
    public async Task RunAsync_FailingRun_RecordsNoCostAtAll()
    {
        // Arrange — a jb that exited non-zero may have given up in seconds, which is no measure of the run
        // the next caller is about to make.
        StubExit(2, "boom");

        // Act
        await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert
        Recorded(JbCostBand.Cold).ShouldBeNull();
    }

    [Fact]
    public async Task RunAsync_RunHitsTheCapWithAComparableRunOnRecord_NamesWhatThatRunCost()
    {
        // Arrange — the figure that turns "the cap was ten minutes" into evidence about whether raising it
        // will help. A solution recorded at eight minutes cold will fit in twelve; one that has never finished
        // will not, and the message can only say which if it knows.
        JbCostRecord.Stamp(_config.SolutionPath, _config.CacheHome, JbCostBand.Cold, TimeSpan.FromSeconds(497), NullLogger.Instance);
        StubTimeout();

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert — beside the advice it already gives, and keyed by the band this run started in.
        exception.Message.ShouldContain(
            "A run that long is almost always a cold ReSharper cache. The last cold run of this solution took "
            + "8 minutes 17 seconds. Scoping the next call with `files` will not help");
    }

    [Fact]
    public async Task RunAsync_RunHitsTheCapWithNothingOnRecord_ReadsExactlyAsItAlwaysHas()
    {
        // Arrange — the first run of a solution is both the one most likely to hit the cap and the one that
        // can never have a figure, so the no-figure message is the common case rather than an edge.
        StubTimeout();

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert — the two sentences still meet with nothing between them.
        exception.Message.ShouldContain(
            "A run that long is almost always a cold ReSharper cache. Scoping the next call with `files` will "
            + "not help");
        exception.Message.ShouldNotContain("The last");
    }

    [Fact]
    public async Task RunAsync_ColdSolutionWithAWarmSibling_SeedsTheCacheBeforeJbStarts()
    {
        // Arrange — the ordering is the whole contract: a copy landing after jb opened the generation is worse
        // than none, so the check is made from inside the spawn rather than after the call returns.
        CacheHomes.PlantWarmDonor(_config.CacheHome, SiblingSolutionPath());
        var seededAtSpawn = false;
        StubExit(0, string.Empty, () => seededAtSpawn = Directory.Exists(SeededGenerationPath()));

        // Act
        await _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        seededAtSpawn.ShouldBeTrue();
    }

    [Fact]
    public async Task TryRunAsync_ColdSolutionWithAWarmSibling_SeedsTheCacheBeforeJbStarts()
    {
        // Arrange — the speculative entry point too: a server launched inside a worktree pre-warms that
        // worktree, and the pre-warm is the very run most likely to be the cold one worth seeding.
        CacheHomes.PlantWarmDonor(_config.CacheHome, SiblingSolutionPath());
        var seededAtSpawn = false;
        StubExit(0, string.Empty, () => seededAtSpawn = Directory.Exists(SeededGenerationPath()));

        // Act
        await _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        seededAtSpawn.ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_RunHitsTheCap_SaysWhoseCapItIsAndNamesTheLever()
    {
        // Arrange — ProcessRunner reports the mechanical fact and stops there, because it does not know
        // whose timeout it was handed. Only the runner can answer the two questions a caller is left with.
        StubTimeout();

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert — every timeout a user meets is one this server chose (an MCP client would have waited
        // far longer), so the message has to disown it, name the variable that moves it, and head off the
        // retry that does not work.
        exception.Message.ShouldStartWith("jb inspectcode timed out after 10 minutes");
        exception.Message.ShouldContain("this server's, not jb's own");
        exception.Message.ShouldContain(JbRunTimeout.Variable);
        exception.Message.ShouldContain("`files` will not help");
    }

    [Fact]
    public async Task RunAsync_RunHitsTheCapWithNoProgressReported_ClaimsNoParticularProgress()
    {
        // Arrange — nothing was watching, so nothing knows how far jb got. cleanupcode lands here too: its
        // per-file output is unrecognisable, so its count is always zero.
        StubTimeout();

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert — the general claim, hedged, and no invented number.
        exception.Message.ShouldContain("The cache keeps most of what this run built");
        exception.Message.ShouldNotContain("by the time it was stopped");
    }

    [Fact]
    public async Task RunAsync_RunHitsTheCapAfterAnalysingFiles_NamesHowFarItGot()
    {
        // Arrange — a jb that reports 40 files and is then killed at the cap. Until there was a count, the
        // promise that "a retry resumes rather than starting over" was made with confidence it had not
        // earned: a run killed having analysed 40 files and one killed at 1,200 read identically.
        StubTimeoutAfterAnalysing(40);

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct, _ => { }));

        // Assert — the count spelled as the progress line spelled it, not as "40 file(s)".
        exception.Message.ShouldContain("jb had reached 40 files by the time it was stopped");
        exception.Message.ShouldContain("a retry resumes from there");
    }

    [Fact]
    public async Task RunAsync_RunHitsTheCap_TheCapLogLineCarriesTheCountToo()
    {
        // Arrange — the log needs it independently of the message: a UserErrorException is deliberately never
        // logged, so without this the only record of how far a killed run got dies with the response.
        CapturingLoggerProvider logs = new();
        JbRunner runner = JbRunners.Create(_processRunner, _runLock, logs: Logs.Capturing(logs));
        StubTimeoutAfterAnalysing(7);

        // Act
        await Should.ThrowAsync<UserErrorException>(() => runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct, _ => { }));

        // Assert — still identified by RunCap alone, with no CacheState or ExitCode of its own: those two
        // properties are how JbRunLoggingTests tells the opening and closing lines apart.
        LogEntry killed = logs.WithProperty("RunCap").ShouldHaveSingleItem();
        killed.Property("FilesSeen").ShouldBe(7);
        logs.WithProperty("ExitCode").ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_RunHitsARaisedCap_ReportsThatCapExactlyAsItWasSet()
    {
        // Arrange — a raised cap that still ran out must say so with the raised number, or the advice to
        // raise it reads as though nothing happened the first time. 455 seconds and not a round number of
        // minutes on purpose: the variable is configured in seconds, so a cap must never be reported back
        // rounded to a value the user did not choose.
        JbRunner runner = JbRunners.Create(_processRunner, _runLock, TimeSpan.FromSeconds(455));
        StubTimeout();

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert
        exception.Message.ShouldStartWith("jb inspectcode timed out after 7 minutes 35 seconds");
    }

    [Fact]
    public async Task TryRunAsync_RunHitsTheCap_ReportsCappedRatherThanAFailureOrASkip()
    {
        // Arrange — the cap protects a caller who is waiting, and nobody waits on speculative work. A big
        // cold solution is both the likeliest to exceed it and the one pre-warming exists for, so treating
        // that as a fault would make the warmer log a warning for its own best-case workload. Nor is it the
        // word for having done nothing: a run killed at the cap spent the whole cap building this cache.
        StubTimeout();

        // Act
        SpeculativeRunOutcome result = await _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        result.ShouldBe(SpeculativeRunOutcome.Capped);
        JbWarmMarker.IsFreshWithin(_config.SolutionPath, _config.CacheHome, RecentlyEnough, NullLogger.Instance).ShouldBeFalse();
    }

    [Fact]
    public async Task TryRunAsync_CacheGenerationAlreadyTaken_ReportsNotStartedWithoutSpawningJb()
    {
        // Arrange — a lease held by someone else, standing in for a real call or another server process.
        using IDisposable? held = _runLock.TryAcquire(_config.SolutionPath, _config.CacheHome);
        held.ShouldNotBeNull();

        // Act
        SpeculativeRunOutcome result = await _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert — not merely "did not wait": speculative work that cannot prove exclusivity never starts jb
        // at all, because a second jb on one cache generation forks a cold one. This is one of the two
        // endings that genuinely cost nothing, and the only kind the summary may call a skip.
        result.ShouldBe(SpeculativeRunOutcome.NotStarted);
        await _processRunner.DidNotReceive().AnyRun();
    }

    [Fact]
    public async Task TryRunAsync_NonZeroExit_ReportsFailedInsteadOfThrowing()
    {
        // Arrange — background work has no channel to raise an error through, so its caller decides. The exit
        // code itself is nobody's to read: the spawn already turned it into the stamp-or-not decision.
        StubExit(4, "something went wrong");

        // Act
        SpeculativeRunOutcome result = await _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        result.ShouldBe(SpeculativeRunOutcome.Failed);
        JbWarmMarker.IsFreshWithin(_config.SolutionPath, _config.CacheHome, RecentlyEnough, NullLogger.Instance).ShouldBeFalse();
    }

    [Fact]
    public async Task TryRunAsync_ItsOwnCallerCancelled_PropagatesInsteadOfReportingASkip()
    {
        // Arrange — a jb killed on the caller's own token, which is how host shutdown reaches a pre-warm.
        // The runner sees the same OperationCanceledException either way, so only the caller's token tells
        // "I was shut down" apart from "a foreground run reclaimed the cache".
        _processRunner
            .AnyRunOf("jb")
            .ThrowsAsync(new OperationCanceledException());
        using CancellationTokenSource callerCancelled = new();
        await callerCancelled.CancelAsync();

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(() => _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], callerCancelled.Token));
    }

    [Fact]
    public async Task TryRunAsync_AfterAForegroundRunHasFinished_RunsAgain()
    {
        // Arrange — a real call has been and gone. Retiring speculative work permanently at that point is
        // what this counter replaced: the moment it is worth most is right after a foreground run has hit
        // the cap, leaving a part-built cache and an idle user.
        StubExit(0, string.Empty);
        await _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Act
        SpeculativeRunOutcome result = await _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        result.ShouldBe(SpeculativeRunOutcome.Completed);
    }

    [Fact]
    public async Task TryRunAsync_ForegroundRunStillInFlight_NeverStartsEvenThoughItsOwnGenerationIsFree()
    {
        // Arrange — a *second* solution, so the run lock cannot be the explanation: its cache generation is
        // free throughout, and the in-flight count is the only thing left that could stop this.
        ResolvedConfig other = Configs.Bare("/sln/Other.sln", _config.CacheHome);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubBlocking(started, release);

        Task<ProcessResult> foreground = _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);
        await started.Task.WaitAsync(Generous, Ct);

        // Act
        SpeculativeRunOutcome result = await _runner.TryRunAsync(other, ["inspectcode", other.SolutionPath], Ct).WaitAsync(Generous, Ct);

        // Assert
        result.ShouldBe(SpeculativeRunOutcome.NotStarted);

        release.SetResult();
        await foreground.WaitAsync(Generous, Ct);
    }

    [Fact]
    public async Task RunAsync_RunHitsTheCap_AnnouncesItWithTheConfigurationThatRanOut()
    {
        // Arrange — the config travels with the signal so a listener warms the solution that actually timed
        // out, rather than whatever the server's working directory would resolve to.
        StubTimeout();
        List<ResolvedConfig> announced = [];
        _runner.ForegroundRunTimedOut += announced.Add;

        // Act
        await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert
        announced.ShouldHaveSingleItem().SolutionPath.ShouldBe(_config.SolutionPath);
    }

    [Fact]
    public async Task RunAsync_RunsThatDidNotTimeOut_AnnounceNothing()
    {
        // Arrange — the signal means "a part-built cache and an idle user", which is true of a run killed at
        // the cap and of nothing else. A clean run left the cache warm; a failed one left a problem no
        // amount of speculative warming addresses.
        List<ResolvedConfig> announced = [];
        _runner.ForegroundRunTimedOut += announced.Add;

        // Act
        StubExit(0, string.Empty);
        await _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        StubExit(7, "boom");
        await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert
        announced.ShouldBeEmpty();
    }

    [Fact]
    public async Task TryRunAsync_RunHitsTheCap_AnnouncesNothing()
    {
        // Arrange — speculative work must never re-arm itself. A pre-warm that announced its own timeout
        // would warm, hit the cap, re-arm, and repeat for the life of the process, with only the run lock
        // pacing it. Recurrence has to advance on something the user did.
        StubTimeout();
        List<ResolvedConfig> announced = [];
        _runner.ForegroundRunTimedOut += announced.Add;

        // Act
        SpeculativeRunOutcome result = await _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        result.ShouldBe(SpeculativeRunOutcome.Capped);
        announced.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_RunHitsTheCap_AnnouncesOnlyOnceTheLeaseIsFreeAndTheCountIsBack()
    {
        // Arrange — both orderings pinned through behaviour rather than by reading privates. A listener that
        // can take the lease proves the lease went first; one whose own speculative run actually starts
        // proves the count was decremented first. Reverse either and nothing throws: the re-armed pass just
        // settles as a skip and the whole feature quietly buys nothing.
        StubTimeout();
        var leaseWasFree = false;
        Task<SpeculativeRunOutcome>? speculative = null;

        _runner.ForegroundRunTimedOut += timedOut =>
        {
            IDisposable? lease = _runLock.TryAcquire(timedOut.SolutionPath, timedOut.CacheHome);
            leaseWasFree = lease is not null;
            lease?.Dispose();

            StubExit(0, string.Empty);
            speculative = _runner.TryRunAsync(timedOut, ["inspectcode", timedOut.SolutionPath], CancellationToken.None);
        };

        // Act
        await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert
        leaseWasFree.ShouldBeTrue();
        Task<SpeculativeRunOutcome> reArmed = speculative.ShouldNotBeNull();
        (await reArmed.WaitAsync(Generous, Ct)).ShouldBe(SpeculativeRunOutcome.Completed);
    }

    [Fact]
    public async Task RunAsync_ListenerThrows_StillReportsTheTimeoutToTheCaller()
    {
        // Arrange — the signal fires while the timeout error is unwinding, so a throwing listener would
        // replace the one message telling the user whose cap it was and which variable moves it.
        StubTimeout();
        _runner.ForegroundRunTimedOut += _ => throw new InvalidOperationException("this listener is broken");

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert
        exception.Message.ShouldStartWith("jb inspectcode timed out");
        exception.Message.ShouldContain(JbRunTimeout.Variable);
    }

    [Fact]
    public async Task TryRunAsync_AlreadyCancelled_NeverStartsJbAtAll()
    {
        // Arrange — ProcessRunner calls Start() before it ever looks at its token, so without the check a
        // pre-warm cancelled in that window forks a jb only to tree-kill and reap it, holding for those
        // seconds the very lease the real call is queueing for.
        StubExit(0, string.Empty);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], cancelled.Token));

        // Assert
        await _processRunner.DidNotReceive().AnyRun();
    }

    /// <summary>
    ///     A run killed at the cap, as <see cref="Zphil.ReSharperCli.Execution.ProcessRunner" /> reports it:
    ///     the mechanical message, carrying no idea of whose cap it was.
    /// </summary>
    private void StubTimeout()
    {
        _processRunner
            .AnyRunOf("jb")
            .ThrowsAsync(new ProcessTimeoutException("'jb' timed out."));
    }

    /// <summary>
    ///     A jb that reports <paramref name="files" /> analysed files and is then killed at the cap — the
    ///     shape of every cold run that runs out of budget, and the only one that can say how far it got.
    /// </summary>
    private void StubTimeoutAfterAnalysing(int files)
    {
        _processRunner
            .AnyRunOf("jb")
            .Returns<ProcessResult>(callInfo =>
            {
                Action<string> onLine = callInfo.OutputLineObserver()!;
                onLine(JbProgressLines.AnalyzingPhaseLine);
                for (var i = 0; i < files; i++) onLine($"Analyzing File{i}.cs");

                throw new ProcessTimeoutException("'jb' timed out.");
            });
    }

    /// <summary>
    ///     A jb that signals when it has started and then parks until told to finish, so a test can hold a
    ///     foreground run open across an assertion.
    /// </summary>
    private void StubBlocking(TaskCompletionSource started, TaskCompletionSource release)
    {
        _processRunner
            .AnyRunOf("jb")
            .Returns(async _ =>
            {
                started.TrySetResult();
                await release.Task;
                return new ProcessResult(0, string.Empty, string.Empty);
            });
    }

    private void StubExit(int exitCode, string standardError, Action? whileRunning = null)
    {
        _processRunner
            .AnyRunOf("jb")
            .Returns(_ =>
            {
                whileRunning?.Invoke();
                return new ProcessResult(exitCode, string.Empty, standardError);
            });
    }

    /// <summary>
    ///     What this solution has on record for <paramref name="band" />. A stubbed run finishes in
    ///     microseconds, so presence is the assertable fact and the figure itself is not.
    /// </summary>
    private TimeSpan? Recorded(JbCostBand band)
    {
        return JbCostRecord.TryRead(_config.SolutionPath, _config.CacheHome, band, NullLogger.Instance);
    }

    /// <summary>Another checkout of the same solution file, which is what makes a donor a donor.</summary>
    private string SiblingSolutionPath()
    {
        return _environment.CreateSolutionPath(Path.GetFileName(_config.SolutionPath));
    }

    /// <summary>Where a cache seeded for this runner's own solution would land.</summary>
    private string SeededGenerationPath()
    {
        return CacheHomes.GenerationPathFor(_config.CacheHome, _config.SolutionPath);
    }
}