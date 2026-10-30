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
///     Pins what <see cref="JbRunner" /> owns for every <c>jb</c> run, whichever entry point starts it.
/// </summary>
public sealed class JbRunnerTests : IDisposable
{
    private static readonly TimeSpan RecentlyEnough = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Brisk = TimeSpan.FromMilliseconds(40);

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
        // from what jb was asked to do.
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
        // Arrange — jb can emit megabytes on a bad run, and only the end of it diagnoses anything, so only
        // the end is quoted into the error.
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
        // Arrange & Act — a defaulted ProcessResult carries a null standard error, and a tail is quoted into
        // a failure report, so a NullReferenceException here would replace the diagnosis with a crash.
        string tail = JbRunner.StandardErrorTail(null);

        // Assert
        tail.ShouldBeEmpty();
    }

    [Fact]
    public void AppendConfigArguments_SettingsFileJbDiscoversItself_OmitsSettingsFlag()
    {
        // Arrange — resolved, but a file jb mounts on its own (the adjacent .DotSettings). Passing it as
        // --settings would re-mount it as a Custom layer above the project layers: inspect would silently
        // demote every {project}.csproj.DotSettings in the solution, and cleanup, which rewrites files, would
        // normalize away the style such a file protects. Each service's exact-order argument pin is what
        // proves it appends through this helper.
        List<string> arguments = [];
        ResolvedConfig config = Configs.With("/sln/App.sln", "/cache", "/sln/App.sln.DotSettings");

        // Act
        JbRunner.AppendConfigArguments(arguments, config);

        // Assert
        arguments.ShouldBe(["--caches-home=/cache"]);
    }

    [Fact]
    public void IncludeArgument_AbsolutePathUnderTheSolution_ReachesJbRelative()
    {
        // Arrange — jb's --include takes relative paths only, so an absolute one matches nothing: cleanupcode
        // exits 3 with "No items were found to cleanup", and an inspect can come back as "No issues found."
        // rather than as any kind of failure.
        string solutionDirectory = Path.GetFullPath("/sln");
        string absolute = Path.Combine(solutionDirectory, "src", "A.cs");

        // Act
        string include = JbRunner.IncludeArgument([absolute], solutionDirectory);

        // Assert
        include.ShouldBe("--include=src/A.cs");
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
    public async Task RunAsync_FailingRun_LeavesEverySidecarAsItFoundIt()
    {
        // Arrange — stamping the warm marker, discharging a reset's tombstone and recording a cost belong to a
        // clean exit alone, and a jb that exited non-zero earns none of them. It warmed nothing worth skipping a
        // pre-warm over. It may have built nothing, so a preceding reset's promise of a cold next run still
        // stands and the next attempt must not shortcut it. And it may have given up in seconds, which is no
        // measure of the run the next caller is about to make.
        JbColdTombstone.Write(_config.SolutionPath, _config.CacheHome, NullLogger.Instance);
        StubExit(2, "boom");

        // Act
        await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert
        _config.ShouldSatisfyAllConditions(
            config => JbWarmMarker.IsFreshWithin(config.SolutionPath, config.CacheHome, RecentlyEnough, NullLogger.Instance).ShouldBeFalse("warm marker stamped"),
            config => JbColdTombstone.Exists(config.SolutionPath, config.CacheHome, NullLogger.Instance).ShouldBeTrue("cache reset discharged"),
            config => File.Exists(JbCostRecord.PathFor(config.SolutionPath, config.CacheHome)).ShouldBeFalse("cost recorded"));
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

        // Assert — the solution path and cache home an operator needs to check the derivation by hand, plus
        // the phrase that says which derivation broke. Pinned here because the runner owns the wording: the
        // marker reports the condition and says nothing about it.
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
        // process. Each has a log of its own, and the latch is per instance for this reason: held on a static,
        // the first session to warn swallows the second's, and the second's log then denies a drift its own
        // run had just met.
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
        // silent about a drift that has just disabled the cache features keyed on that hash.
        CapturingLoggerProvider logs = new();
        JbRunner runner = JbRunners.Create(_processRunner, _runLock, logs: Logs.Capturing(logs));
        StubExit(0, string.Empty);

        // Act
        SpeculativeRunOutcome result = await runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert — speculative work reports rather than throws, but a drift is still worth a warning: the
        // condition is about jb rather than about the pass that happened to notice it.
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
        JbColdTombstone.Exists(_config.SolutionPath, _config.CacheHome, NullLogger.Instance).ShouldBeTrue();
        StubExit(0, string.Empty);

        // Act
        await _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        JbColdTombstone.Exists(_config.SolutionPath, _config.CacheHome, NullLogger.Instance).ShouldBeFalse();
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
    }

    [Fact]
    public async Task RunAsync_RunFiredByATransplant_RecordsItAsSeededRatherThanCold()
    {
        // Arrange — a solution about to be seeded reads as cold when its band is read, before the transplant
        // runs. A seeded run and a cold one cost differently, so filing this run by that reading would quote
        // its figure to the next cold run.
        CacheHomes.PlantWarmDonor(_config.CacheHome, SiblingSolutionPath());
        StubExit(0, string.Empty);

        // Act
        await _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        Recorded(JbCostBand.Seeded).ShouldNotBeNull();
        Recorded(JbCostBand.Cold).ShouldBeNull();
    }

    [Fact]
    public async Task RunAsync_WarmRun_RecordsNoCost()
    {
        // Arrange — warm is not a band: the figure a warm run leaves does not predict the next one's duration,
        // so there is nothing here worth a file. The record is not written, rather than written and then
        // declined on the way out.
        CacheHomes.PlantWarmDonor(_config.CacheHome, _config.SolutionPath);
        StubExit(0, string.Empty);

        // Act
        await _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        File.Exists(JbCostRecord.PathFor(_config.SolutionPath, _config.CacheHome)).ShouldBeFalse();
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
        // can never have a figure, so the no-figure message is the common case rather than an edge. No
        // progress callback is passed, so nothing counts how far jb got: cleanupcode lands here too, because
        // its per-file output is unrecognisable and its count is always zero.
        StubTimeout();

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert — every timeout a user meets is one this server chose (an MCP client would have waited far
        // longer), so the message disowns it, names the variable that moves it, and heads off the scoped
        // retry that does not work. With nothing on record, no figure sits between the cold-cache sentence
        // and the scoping one, and the closing claim about the cache is the general one, with no count.
        exception.Message.ShouldBe(
            "jb inspectcode timed out after 10 minutes and was stopped.\n"
            + "That cap is this server's, not jb's own: raise it by setting RESHARPER_MCP_TIMEOUT_SECS (in seconds) "
            + "in this server's env block in your MCP client config, then restart the server.\n"
            + "A run that long is almost always a cold ReSharper cache. "
            + "Scoping the next call with `files` will not help — jb analyses the whole solution whatever "
            + "the report is narrowed to. "
            + "The cache keeps most of what this run built, so a retry resumes rather than starting over.");
    }

    [Fact]
    public async Task RunAsync_WarmInspectHitsTheCap_BlamesTheAnalysisRatherThanAColdCache()
    {
        // Arrange — a warm cache was building nothing, so the cold-cache diagnosis is wrong and a retry has
        // nothing to resume. The analysis stays solution-wide, so scoping still cannot shorten it.
        CacheHomes.PlantWarmDonor(_config.CacheHome, _config.SolutionPath);
        StubTimeout();

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert
        exception.Message.ShouldBe(
            "jb inspectcode timed out after 10 minutes and was stopped.\n"
            + "That cap is this server's, not jb's own: raise it by setting RESHARPER_MCP_TIMEOUT_SECS (in seconds) "
            + "in this server's env block in your MCP client config, then restart the server.\n"
            + "The ReSharper cache was warm, so this was not the slow first run: a warm analysis of this "
            + "solution takes longer than the cap, and raising the cap is the fix. Scoping the next call with "
            + "`files` will not help — jb analyses the whole solution whatever the report is narrowed to.");
    }

    [Fact]
    public async Task RunAsync_WarmCleanupHitsTheCap_PointsAtTheFileCountRatherThanAColdCache()
    {
        // Arrange — a warm cleanup's time grows with the files it rewrites, so narrowing them is the remedy
        // that works, and the message must not rule it out.
        CacheHomes.PlantWarmDonor(_config.CacheHome, _config.SolutionPath);
        StubTimeout();

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, [CleanupService.Subcommand, _config.SolutionPath], Ct));

        // Assert — the cold-cache advice first, so a leak of it reds by name before the whole-string diff.
        exception.Message.ShouldNotContain("cold ReSharper cache");
        exception.Message.ShouldNotContain("will not help");
        exception.Message.ShouldBe(
            "jb cleanupcode timed out after 10 minutes and was stopped.\n"
            + "That cap is this server's, not jb's own: raise it by setting RESHARPER_MCP_TIMEOUT_SECS (in seconds) "
            + "in this server's env block in your MCP client config, then restart the server.\n"
            + "The ReSharper cache was warm, so this was not the slow first run. A cleanup's time grows with the "
            + "number of files it rewrites, so narrow `files` to the ones that changed, or split them across "
            + "several calls, before raising the cap. Files this run reached before it was stopped may already "
            + "have been rewritten.");
    }

    [Fact]
    public async Task RunAsync_ColdCleanupHitsTheCap_KeepsTheColdCacheDiagnosisAndWarnsOfRewrittenFiles()
    {
        // Arrange — a cold cleanup builds the whole solution's cache before it rewrites anything, so the cold
        // wording still holds for it: of the cache states, only warm changes the advice. The cap can still fall
        // after the rewriting began, so the caller is told to check the working tree before retrying.
        StubTimeout();

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, [CleanupService.Subcommand, _config.SolutionPath], Ct));

        // Assert
        exception.Message.ShouldBe(
            "jb cleanupcode timed out after 10 minutes and was stopped.\n"
            + "That cap is this server's, not jb's own: raise it by setting RESHARPER_MCP_TIMEOUT_SECS (in seconds) "
            + "in this server's env block in your MCP client config, then restart the server.\n"
            + "A run that long is almost always a cold ReSharper cache. "
            + "Scoping the next call with `files` will not help — jb analyses the whole solution whatever "
            + "the report is narrowed to. "
            + "The cache keeps most of what this run built, so a retry resumes rather than starting over. "
            + "Files this run reached before it was stopped may already have been rewritten.");
    }

    [Fact]
    public async Task RunAsync_CleanupThroughAFilterHitsTheCapCold_DropsTheScopingClaimAndRearmsThePreWarm()
    {
        // Arrange — a cold cache. A run through a filter opens the solution's own generation, so it gets the cold
        // diagnosis and the pre-warm is worth re-arming, but it loaded only its files' projects, so scoping helps.
        SolutionFilterPlan filter = FilterIn(_environment.CreateTempDirectory());
        StubTimeout();
        List<ResolvedConfig> announced = [];
        _runner.ForegroundRunTimedOut += announced.Add;

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, [CleanupService.Subcommand, filter.Path], Ct, filter: filter));

        // Assert
        exception.Message.ShouldBe(
            "jb cleanupcode timed out after 10 minutes and was stopped.\n"
            + "That cap is this server's, not jb's own: raise it by setting RESHARPER_MCP_TIMEOUT_SECS (in seconds) "
            + "in this server's env block in your MCP client config, then restart the server.\n"
            + "A run that long is almost always a cold ReSharper cache. "
            + "The cache keeps most of what this run built, so a retry resumes rather than starting over. "
            + "Files this run reached before it was stopped may already have been rewritten.");
        announced.ShouldHaveSingleItem().ShouldBe(_config);
    }

    [Fact]
    public async Task RunAsync_WithAFilter_WritesItUnderTheSolutionsLeaseBeforeJbStarts()
    {
        // Arrange — the solution's lease held by another run, which is the only state in which a second session
        // could be rewriting the same file: jb opens the solution's generation through a filter, so that lease is
        // the one a filtered run queues on.
        SolutionFilterPlan filter = FilterIn(_environment.CreateTempDirectory());
        string? atSpawn = null;
        _processRunner
            .AnyRunOf("jb")
            .Returns(_ =>
            {
                atSpawn = File.ReadAllText(filter.Path);
                return JbStubs.Success;
            });
        JbRunner runner = JbRunners.Create(_processRunner, _runLock, JbRunners.Yield(), heartbeat: Brisk);
        RecordingSink<string> lines = new(Generous);

        // Act — waits for the beat that says the run is queued on the lease, which comes only once the wait has
        // outlasted JbRunLock.NotableWait, rather than for an interval that may end before the run gets there.
        Task run;
        await using (CacheHomes.HoldLockFile(_config.CacheHome, _config.SolutionPath))
        {
            run = runner.RunAsync(_config, [CleanupService.Subcommand, filter.Path], Ct, lines.Record, filter);
            await lines.FirstAsync(line => line.Contains("waiting for another run"), "a beat naming the queue wait", Ct);

            // Assert — nothing written while the lease was someone else's.
            File.Exists(filter.Path).ShouldBeFalse("the filter was written before its lease was held");
        }

        await run;

        // Assert — and in place by the time jb opened it.
        atSpawn.ShouldBe(filter.Content);
    }

    [Fact]
    public async Task RunAsync_FilterThatCannotBeWritten_FailsNamingTheSwitchThatTurnsItOff()
    {
        // Arrange — the filter's directory would have to be created where a file already is.
        SolutionFilterPlan filter = FilterIn(CacheHomes.BlockedCacheHome(_environment));

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, [CleanupService.Subcommand, filter.Path], Ct, filter: filter));

        // Assert
        exception.Message.ShouldStartWith($"Could not write the solution filter this cleanup runs through to \"{filter.Path}\" (");
        exception.Message.ShouldEndWith(
            "). To clean up through the whole solution instead, set RESHARPER_MCP_CLEANUP_FILTER=off in this server's "
            + "env block in your MCP client config, then restart the server.");
        await _processRunner.DidNotReceive().AnyRunOf("jb");
    }

    [Fact]
    public async Task RunAsync_ColdSolutionWithAWarmSibling_SeedsTheCacheBeforeJbStarts()
    {
        // Arrange — the ordering is the contract: a copy landing after jb opened the generation is worse than
        // none, so the check is made from inside the spawn rather than after the call returns.
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
        // worktree, and the pre-warm is the run most likely to be the cold one worth seeding.
        CacheHomes.PlantWarmDonor(_config.CacheHome, SiblingSolutionPath());
        var seededAtSpawn = false;
        StubExit(0, string.Empty, () => seededAtSpawn = Directory.Exists(SeededGenerationPath()));

        // Act
        await _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        seededAtSpawn.ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_RunHitsTheCapAfterAnalysingFiles_NamesHowFarItGot()
    {
        // Arrange — without the count, the promise that "a retry resumes rather than starting over" is made
        // with confidence it has not earned: a run killed having analysed 40 files and one killed at 1,200
        // read identically.
        StubTimeoutAfterAnalysing(40);

        // Act — with a progress callback, even one that drops every line: the runner watches jb's output only
        // when it has somewhere to report progress, so without one there is no count to report.
        var exception = await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct, _ => { }));

        // Assert — the count spelled as the progress line spelled it, not as "40 file(s)".
        exception.Message.ShouldContain("jb had reached 40 files by the time it was stopped");
        exception.Message.ShouldContain("a retry resumes from there");
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

        // Assert — not only "did not wait": speculative work that cannot prove exclusivity never starts jb at
        // all, because a second jb on one cache generation forks a cold one. Nothing ran and nothing was
        // spent, which is the only ending the summary may call a skip.
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
        // Cancelled from inside the run, so the cancellation meets a jb in flight rather than the guard before
        // the spawn.
        using var callerCancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _processRunner
            .AnyRunOf("jb")
            .Returns(callInfo =>
            {
                // The run is awaited below before the using disposes, so the capture outlives nothing.
                // ReSharper disable once AccessToDisposedClosure
                callerCancelled.Cancel();
                callInfo.Token().ThrowIfCancellationRequested();

                return JbStubs.Success;
            });

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(() => _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], callerCancelled.Token));
        await _processRunner.Received(1).AnyRunOf("jb");
    }

    [Fact]
    public async Task RunAsync_RunHitsTheCap_AnnouncesItWithTheConfigurationThatRanOut()
    {
        // Arrange — the config travels with the signal so a listener warms the solution that timed out,
        // rather than whatever the server's working directory would resolve to.
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
        // can take the lease proves the lease went first; one whose own speculative run starts proves the
        // count was decremented first. Reverse either and nothing throws: the re-armed pass settles as a skip
        // and the feature silently buys nothing.
        StubTimeout();
        var leaseWasFree = false;
        Task<SpeculativeRunOutcome>? speculative = null;

        _runner.ForegroundRunTimedOut += timedOut =>
        {
            IDisposable? lease = _runLock.TryAcquire(timedOut.SolutionPath, timedOut.CacheHome);
            leaseWasFree = lease is not null;
            lease?.Dispose();

            StubExit(0, string.Empty);
            speculative = _runner.TryRunAsync(timedOut, ["inspectcode", timedOut.SolutionPath], Ct);
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
        // seconds the lease the foreground call is queueing for.
        StubExit(0, string.Empty);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], cancelled.Token));

        // Assert
        await _processRunner.DidNotReceive().AnyRun();
    }

    /// <summary>Returns a filter of one project of two, to be written under <paramref name="directory" />.</summary>
    private static SolutionFilterPlan FilterIn(string directory)
    {
        return new SolutionFilterPlan(
            Path.Combine(directory, "cleanup-filters", "App.cleanup-0123456789abcdef.slnf"),
            """{ "solution": { "path": "/sln/App.sln", "projects": [ "A/A.csproj" ] } }""",
            1,
            2);
    }

    /// <summary>
    ///     Stubs a run killed at the cap as <see cref="Zphil.ReSharperCli.Execution.ProcessRunner" /> reports
    ///     it: the mechanical message, carrying no idea of whose cap it was.
    /// </summary>
    private void StubTimeout()
    {
        _processRunner
            .AnyRunOf("jb")
            .ThrowsAsync(new ProcessTimeoutException("'jb' timed out."));
    }

    /// <summary>
    ///     Stubs a jb that reports <paramref name="files" /> analysed files and is then killed at the cap, the
    ///     way a cold run that runs out of budget looks when its output is watched.
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
    ///     Returns what this solution has on record for <paramref name="band" />.
    /// </summary>
    /// <remarks>
    ///     A stubbed run finishes in microseconds, so presence is the assertable fact and the figure itself is
    ///     not.
    /// </remarks>
    private TimeSpan? Recorded(JbCostBand band)
    {
        return JbCostRecord.TryRead(_config.SolutionPath, _config.CacheHome, band, NullLogger.Instance);
    }

    /// <summary>Returns a same-named solution path in another checkout, whose warm cache can be a donor.</summary>
    private string SiblingSolutionPath()
    {
        return _environment.CreateSolutionPath(Path.GetFileName(_config.SolutionPath));
    }

    /// <summary>Returns where a cache seeded for this runner's own solution would land.</summary>
    private string SeededGenerationPath()
    {
        return CacheHomes.GenerationPathFor(_config.CacheHome, _config.SolutionPath);
    }
}