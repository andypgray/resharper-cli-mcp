using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Services;

/// <summary>
///     <see cref="CacheResetService" /> deletes directories a caller never named, on the strength of a
///     derivation from <c>jb</c>'s undocumented naming, so these pin the properties that make that safe: it
///     drops exactly the generations whose names carry this solution path's own hash, it reports rather than
///     touches the ones that do not, it will not delete a cache generation while a <c>jb</c> run holds it,
///     and it leaves behind the record that keeps the next run cold. The one case that turns those last two
///     around is a reclaim, where the solution file is gone: the proof is unchanged, since it never read the
///     file, but there is no next run to keep cold, so no record is left behind.
/// </summary>
public sealed class CacheResetServiceTests : IDisposable
{
    /// <summary>The one beat an uncontended reset can fit, spelled once for the tests that pin it.</summary>
    private const string Starting = "cache reset on App.sln: starting";

    /// <summary>Short enough that a test sees several beats, long enough not to be flaky under load.</summary>
    private static readonly TimeSpan Brisk = TimeSpan.FromMilliseconds(40);

    /// <summary>Long enough that only a genuine hang reaches it.</summary>
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private readonly string _cacheHome;
    private readonly ResolvedConfig _config;
    private readonly FakeEnvironment _environment = new();
    private readonly CacheResetService _service = JbRunners.Reset(JbRunners.Lock(TimeSpan.FromSeconds(1)), JbRunners.Yield());

    public CacheResetServiceTests()
    {
        _cacheHome = _environment.CreateTempDirectory();
        _config = ConfigFor("App.sln", _cacheHome);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public async Task RunAsync_SolutionWithAForkedGeneration_DropsBothAndLeavesNeighboursAlone()
    {
        // Arrange — the reclaim case: the generation in use plus the cold ".01" fork a concurrent jb left
        // behind, beside a sibling solution whose name shares the prefix and an unrelated one.
        string ours = CacheHomes.PlantGenerationFor(_cacheHome, _config.SolutionPath);
        string fork = CacheHomes.PlantFork(_cacheHome, ours);
        CacheHomes.PlantGeneration(_cacheHome, "_App.Core.400500600.00");
        CacheHomes.PlantGeneration(_cacheHome, "_Other.99.00");

        // Act
        CacheResetOutcome outcome = await _service.RunAsync(_config, Ct);

        // Assert
        outcome.Dropped.ShouldBe([Path.GetFileName(ours), Path.GetFileName(fork)]);
        outcome.LeftAlone.ShouldBeEmpty();
        outcome.Failures.ShouldBeEmpty();
        Directory.Exists(ours).ShouldBeFalse();
        Directory.Exists(fork).ShouldBeFalse();
        Directory.Exists(Path.Combine(_cacheHome, "_App.Core.400500600.00")).ShouldBeTrue();
        Directory.Exists(Path.Combine(_cacheHome, "_Other.99.00")).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_AnotherCheckoutsGenerationBesideOurs_DropsOnlyOursAndSaysSo()
    {
        // Arrange — two checkouts of one repository sharing a cache home: same solution file name, different
        // paths, so jb hashed them apart. Reproducing that hash is what turns this from an ambiguity the tool
        // used to refuse on into an ordinary answer.
        string ours = CacheHomes.PlantGenerationFor(_cacheHome, _config.SolutionPath);
        string theirs = CacheHomes.PlantGenerationFor(_cacheHome, _environment.CreateSolutionPath("App.sln"));

        // Act
        CacheResetOutcome outcome = await _service.RunAsync(_config, Ct);

        // Assert — and the other checkout's cache is still there, which is the point of naming it rather than
        // deleting it.
        outcome.Dropped.ShouldBe([Path.GetFileName(ours)]);
        Names(outcome.LeftAlone).ShouldBe([Path.GetFileName(theirs)]);
        Directory.Exists(theirs).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_OnlyAnotherCheckoutsGenerations_DeletesNothingRatherThanPickingOne()
    {
        // Arrange — this solution has never been analysed, and the generations sharing its file name belong
        // to someone else. A hash that matches nothing must delete nothing: there is no closest match here,
        // only a wrong one.
        string theirs = CacheHomes.PlantGenerationFor(_cacheHome, _environment.CreateSolutionPath("App.sln"));

        // Act
        CacheResetOutcome outcome = await _service.RunAsync(_config, Ct);

        // Assert
        outcome.Dropped.ShouldBeEmpty();
        outcome.Failures.ShouldBeEmpty();
        Names(outcome.LeftAlone).ShouldBe([Path.GetFileName(theirs)]);
        Directory.Exists(theirs).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_DroppingAGeneration_ClearsTheWarmMarkerThatDescribedIt()
    {
        // Arrange — the marker says "a jb run against this generation succeeded recently", which is what stops
        // the next session pre-warming. Leaving it behind would make the next session's first real call pay
        // the cold run this reset just guaranteed — and would leave this solution advertised as a donor for a
        // cache that no longer exists.
        CacheHomes.PlantWarmDonor(_cacheHome, _config.SolutionPath);
        JbWarmMarker.IsFreshWithin(_config.SolutionPath, _cacheHome, TimeSpan.FromHours(1), NullLogger.Instance).ShouldBeTrue();

        // Act
        await _service.RunAsync(_config, Ct);

        // Assert
        JbWarmMarker.IsFreshWithin(_config.SolutionPath, _cacheHome, TimeSpan.FromHours(1), NullLogger.Instance).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_DroppingAGeneration_ClearsTheRecordedCostsWithTheMarker()
    {
        // Arrange — the durations on record describe the lineage this reset is ending. A seeded figure belongs
        // to a cache copied from a sibling, which the reset has just deleted, so leaving it behind would have
        // the next call quote the seeding premium at a caller about to build the cache from nothing.
        CacheHomes.PlantWarmDonor(_cacheHome, _config.SolutionPath);
        JbCostRecord.Stamp(_config.SolutionPath, _cacheHome, JbCostBand.Seeded, TimeSpan.FromSeconds(456), NullLogger.Instance);

        // Act
        await _service.RunAsync(_config, Ct);

        // Assert
        JbCostRecord.TryRead(_config.SolutionPath, _cacheHome, JbCostBand.Seeded, NullLogger.Instance).ShouldBeNull();
    }

    [Fact]
    public async Task RunAsync_EvenWithNothingToDrop_RecordsThatTheNextRunIsMeantToBeCold()
    {
        // Arrange — an already-reset solution. "Nothing was deleted" is not the same as "no reset happened":
        // the caller asked for cold, and the record is what stops a later run seeding this cache from a
        // sibling checkout instead of rebuilding it.

        // Act
        CacheResetOutcome outcome = await _service.RunAsync(_config, Ct);

        // Assert
        outcome.Dropped.ShouldBeEmpty();
        JbColdTombstone.Exists(_config.SolutionPath, _cacheHome, NullLogger.Instance).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_SolutionFileGone_DropsItsGenerationsAndRecordsNoReset()
    {
        // Arrange — a worktree that has been removed, whose cache generation outlived it. Nothing else can
        // name that generation: it is addressed by the hash of a path with no file on it, and the ownership
        // proof never read the file in the first place.
        ResolvedConfig removed = RemovedCheckout();
        string theirs = CacheHomes.PlantGenerationFor(_cacheHome, removed.SolutionPath);

        // Act
        CacheResetOutcome outcome = await _service.RunAsync(removed, Ct);

        // Assert — dropped, and no tombstone: there is no next run against that path to keep cold, and one
        // left there would outlive the checkout and deny the seeding to whatever is created there later.
        outcome.Dropped.ShouldBe([Path.GetFileName(theirs)]);
        outcome.SolutionFileExists.ShouldBeFalse();
        Directory.Exists(theirs).ShouldBeFalse();
        JbColdTombstone.Exists(removed.SolutionPath, _cacheHome, NullLogger.Instance).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_SolutionFileGone_ClearsATombstoneAnEarlierResetLeft()
    {
        // Arrange — the ordinary sequence: a real reset of a live checkout, then the checkout deleted, then
        // the reclaim. The tombstone the first call wrote is a promise about a run that is never going to
        // happen, and leaving it would hold back the seeding of a worktree re-created at that path.
        ResolvedConfig removed = RemovedCheckout();
        JbColdTombstone.Write(removed.SolutionPath, _cacheHome, NullLogger.Instance);

        // Act
        await _service.RunAsync(removed, Ct);

        // Assert
        JbColdTombstone.Exists(removed.SolutionPath, _cacheHome, NullLogger.Instance).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_SolutionFileGone_LeavesTheLiveCheckoutsGenerationAlone()
    {
        // Arrange — the reason a reclaim is worth having and the reason it has to stay narrow: the deleted
        // worktree's cache sits in the same cache home as the checkout still being worked in, under a
        // directory name that differs only in the hash.
        ResolvedConfig removed = RemovedCheckout();
        string theirs = CacheHomes.PlantGenerationFor(_cacheHome, removed.SolutionPath);
        string live = CacheHomes.PlantGenerationFor(_cacheHome, _config.SolutionPath);

        // Act
        CacheResetOutcome outcome = await _service.RunAsync(removed, Ct);

        // Assert
        outcome.Dropped.ShouldBe([Path.GetFileName(theirs)]);
        Names(outcome.LeftAlone).ShouldBe([Path.GetFileName(live)]);
        Directory.Exists(live).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_SolutionFileGoneAndNothingCached_ReportsNothingToDrop()
    {
        // Arrange — a path guessed at, or one whose cache a previous reclaim already took. The tool is
        // idempotent either way, and a hash matching nothing drops nothing.
        ResolvedConfig removed = RemovedCheckout();

        // Act
        CacheResetOutcome outcome = await _service.RunAsync(removed, Ct);

        // Assert
        outcome.Dropped.ShouldBeEmpty();
        outcome.SolutionFileExists.ShouldBeFalse();
        JbColdTombstone.Exists(removed.SolutionPath, _cacheHome, NullLogger.Instance).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_NeighbourWarmedForAPathThatExists_SaysSo()
    {
        // Arrange — the other checkout someone is still working in. Naming it is what stops the left-alone
        // list reading as directories the tool could not account for.
        string theirSolution = _environment.CreateCheckout("App.sln");
        string theirs = CacheHomes.PlantWarmDonor(_cacheHome, theirSolution);

        // Act
        CacheResetOutcome outcome = await _service.RunAsync(_config, Ct);

        // Assert
        LeftAloneGeneration reported = outcome.LeftAlone.ShouldHaveSingleItem();
        reported.Name.ShouldBe(Path.GetFileName(theirs));
        reported.Attribution.ShouldBe(LeftAloneAttribution.CheckoutPresent);
        reported.LastWarmedFor.ShouldBe(theirSolution);
    }

    [Fact]
    public async Task RunAsync_NeighbourWarmedForAPathThatIsGone_SaysItNoLongerExists()
    {
        // Arrange — the whole point of recording the path: this generation is reclaimable, and no directory
        // name can say so, because the hash jb names it by is one-way.
        string theirSolution = _environment.CreateSolutionPath("App.sln");
        string theirs = CacheHomes.PlantWarmDonor(_cacheHome, theirSolution);

        // Act
        CacheResetOutcome outcome = await _service.RunAsync(_config, Ct);

        // Assert
        LeftAloneGeneration reported = outcome.LeftAlone.ShouldHaveSingleItem();
        reported.Name.ShouldBe(Path.GetFileName(theirs));
        reported.Attribution.ShouldBe(LeftAloneAttribution.CheckoutGone);
        reported.LastWarmedFor.ShouldBe(theirSolution);
    }

    [Fact]
    public async Task RunAsync_NeighbourWithNoMarker_ReportsNoRunOnRecord()
    {
        // Arrange — a run killed at the cap, or a jb started outside this server's queue. There is a
        // directory and no record of who made it, which is a fact about this server rather than a guess to
        // dress up as one.
        string theirs = CacheHomes.PlantGenerationFor(_cacheHome, _environment.CreateSolutionPath("App.sln"));

        // Act
        CacheResetOutcome outcome = await _service.RunAsync(_config, Ct);

        // Assert
        LeftAloneGeneration reported = outcome.LeftAlone.ShouldHaveSingleItem();
        reported.Name.ShouldBe(Path.GetFileName(theirs));
        reported.Attribution.ShouldBe(LeftAloneAttribution.NoRunOnRecord);
        reported.LastWarmedFor.ShouldBeNull();
    }

    [Fact]
    public async Task RunAsync_NeighbourStampedByAnEarlierBuild_ReportsThatNoPathWasRecorded()
    {
        // Arrange — every marker on disk the first time a server carrying this runs. Kept apart from "no run
        // on record" because they call for different things: this one fixes itself on the next clean run.
        string theirSolution = _environment.CreateSolutionPath("App.sln");
        string theirs = CacheHomes.PlantWarmDonorFromAnEarlierBuild(_cacheHome, theirSolution);

        // Act
        CacheResetOutcome outcome = await _service.RunAsync(_config, Ct);

        // Assert
        LeftAloneGeneration reported = outcome.LeftAlone.ShouldHaveSingleItem();
        reported.Attribution.ShouldBe(LeftAloneAttribution.PathNotRecorded);
        reported.LastWarmedFor.ShouldBeNull();
    }

    [Fact]
    public async Task RunAsync_NothingCachedForThisSolution_ReportsNothingRatherThanFailing()
    {
        // Arrange — an already-reset solution, or one that has never been analysed. The tool is idempotent, so
        // the second call in a row is a normal thing to do.
        CacheHomes.PlantGeneration(_cacheHome, "_Other.99.00");

        // Act
        CacheResetOutcome outcome = await _service.RunAsync(_config, Ct);

        // Assert
        outcome.Dropped.ShouldBeEmpty();
        outcome.LeftAlone.ShouldBeEmpty();
        outcome.Failures.ShouldBeEmpty();
        Directory.Exists(Path.Combine(_cacheHome, "_Other.99.00")).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_GenerationThatWillNotDelete_ReportsItRatherThanFailingTheCall()
    {
        // Arrange — the generation made undeletable, which is what a jb this server does not know about looks
        // like from here. A partly-deleted cache jb rebuilds is a better outcome than a call that throws
        // having already removed most of one, so the failure is reported and the tool stays idempotent.
        string ours = CacheHomes.PlantGenerationFor(_cacheHome, _config.SolutionPath);
        using IDisposable held = CacheHomes.BlockDeletionOf(ours);

        // Act
        CacheResetOutcome outcome = await _service.RunAsync(_config, Ct);

        // Assert — named, carrying the filesystem's own reason; fitting it onto the report's one line is the
        // formatter's job.
        outcome.Dropped.ShouldBeEmpty();
        CacheResetFailure failure = outcome.Failures.ShouldHaveSingleItem();
        failure.Name.ShouldBe(Path.GetFileName(ours));
        failure.Reason.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task RunAsync_AJbRunHoldsTheCacheGeneration_WaitsAndThenRefusesWithoutDeleting()
    {
        // Arrange — the whole reason this is a server-side tool rather than advice to delete a glob. The held
        // lock file is what another session's live jb looks like from here, and the short-wait service is
        // what lets the test hit the cap.
        string ours = CacheHomes.PlantGenerationFor(_cacheHome, _config.SolutionPath);
        await using FileStream held = CacheHomes.HoldLockFile(_cacheHome, _config.SolutionPath);
        CacheResetService service = JbRunners.Reset(JbRunners.Lock(TimeSpan.FromMilliseconds(250)), JbRunners.Yield());

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => service.RunAsync(_config, Ct));

        // Assert — it queued on the run rather than deleting the cache underneath it, and gave up intact.
        exception.Message.ShouldContain("Another jb run already holds the ReSharper cache");
        Directory.Exists(ours).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_QueuedBehindAnotherRun_ReportsTheWaitUntilTheLockFrees()
    {
        // Arrange — the silence this exists to break. A reset takes the same lock a jb run does, so it can
        // sit here for the whole wait budget, and it has no process of its own whose output could stand in
        // for a report. The held lock file is what another session's live jb looks like from here.
        CacheHomes.PlantGenerationFor(_cacheHome, _config.SolutionPath);
        FileStream held = CacheHomes.HoldLockFile(_cacheHome, _config.SolutionPath);
        RecordingSink<string> lines = new(Generous);
        CacheResetService service = JbRunners.Reset(
            JbRunners.Lock(Generous), JbRunners.Yield(), heartbeat: Brisk);

        // Act — the wait has to outlast JbRunLock.NotableWait before a beat stops reading "starting", so
        // this genuinely waits the second out rather than shortening the threshold to suit itself.
        Task<CacheResetOutcome> reset = service.RunAsync(_config, Ct, lines.Record);
        try
        {
            await lines.WaitUntilAsync(
                () => lines.Items.Any(line => line.Contains("waiting for another run")),
                "a beat naming the queue wait",
                Ct);
        }
        finally
        {
            // Released on every path rather than at scope exit: the reset is queued on this very lock, so a
            // failed wait would otherwise leave it parked behind a file nothing was going to let go of.
            await held.DisposeAsync();
        }

        CacheResetOutcome outcome = await reset;

        // Assert — it named the wait while serving it out, then went through with the delete.
        lines.Items[0].ShouldBe(Starting);
        lines.Items.ShouldContain(line => line.StartsWith(
            "cache reset on App.sln: waiting for another run on this solution's ReSharper cache — ",
            StringComparison.Ordinal));
        outcome.Dropped.ShouldHaveSingleItem();

        // And no cap anywhere in them: a reset spends none of the run budget, so a message charging its wait
        // against that cap would send a caller to raise the one number that was never the problem.
        lines.Items.ShouldAllBe(line => !line.Contains("cap"));
    }

    [Fact]
    public async Task RunAsync_RefusedAfterTheFullWait_StopsBeatingBeforeTheErrorSurfaces()
    {
        // Arrange — the wait runs out and the call fails. A beat landing after that reports against a
        // request already answered, where the MCP session discards the send task and the fault surfaces only
        // as an unobserved exception; scoping the reporter to the acquire is what rules it out.
        await using FileStream held = CacheHomes.HoldLockFile(_cacheHome, _config.SolutionPath);
        RecordingSink<string> lines = new(Generous);
        CacheResetService service = JbRunners.Reset(
            JbRunners.Lock(TimeSpan.FromMilliseconds(250)), JbRunners.Yield(), heartbeat: Brisk);

        // Act — beats first, so this pins a reporter that stopped rather than one that never started.
        Task<CacheResetOutcome> refused = service.RunAsync(_config, Ct, lines.Record);
        await lines.WaitForAsync(1, Ct);
        await Should.ThrowAsync<UserErrorException>(() => refused);
        int atFailure = lines.Count;

        // Assert — many would-be beats fit in this delay at the interval above, and none of them land.
        await Task.Delay(Brisk * 10, Ct);
        lines.Count.ShouldBe(atFailure);
    }

    [Fact]
    public async Task RunAsync_UncontendedCall_SaysOnlyStarting()
    {
        // Arrange — an uncontended acquire is sub-millisecond, so the immediate first beat is the only one
        // that can fit, and it must not blame another session for a wait that never happened. The brisk
        // interval is what makes the other half of the claim testable: the deletes are outside the
        // reporter's scope, so however long they take, nothing beats over them.
        RecordingSink<string> lines = new(Generous);
        CacheHomes.PlantGenerationFor(_cacheHome, _config.SolutionPath);
        CacheResetService service = JbRunners.Reset(
            JbRunners.Lock(TimeSpan.FromSeconds(1)), JbRunners.Yield(), heartbeat: Brisk);

        // Act
        CacheResetOutcome outcome = await service.RunAsync(_config, Ct, lines.Record);
        await Task.Delay(Brisk * 5, Ct);

        // Assert — the reset did its work and said at most one thing about it. At most, because the first
        // beat is queued on the thread pool and disposal can outrun it under load; the pin is that nothing
        // other than "starting" is reachable on this path.
        outcome.Dropped.ShouldHaveSingleItem();
        lines.Items.ShouldAllBe(line => line == Starting);
        lines.Count.ShouldBeLessThanOrEqualTo(1);
    }

    /// <summary>
    ///     A config naming a solution in a directory of this test's own. Only the solution path and cache home
    ///     matter here: a reset runs no <c>jb</c>, so settings, extensions, and the profile play no part.
    /// </summary>
    /// <summary>
    ///     The reset says what it did, at <see cref="LogLevel.Information" />.
    /// </summary>
    /// <remarks>
    ///     It is the one tool that spawns no <c>jb</c>, and was the one that left no trace at all. Since a
    ///     reset is precisely why the <em>next</em> call runs cold, a log without it shows that call taking
    ///     minutes against a cache home that a moment earlier looked populated, for no visible reason.
    /// </remarks>
    [Fact]
    public async Task RunAsync_WhateverItDropped_RecordsTheOutcomeAtInformation()
    {
        // Arrange — one generation of this solution's own, and one belonging to another checkout, so the line
        // has both halves of the split to report.
        CapturingLoggerProvider logs = new();
        string ours = CacheHomes.PlantGenerationFor(_cacheHome, _config.SolutionPath);
        CacheHomes.PlantGenerationFor(_cacheHome, _environment.CreateSolutionPath("App.sln"));

        CacheResetService service = JbRunners.Reset(
            JbRunners.Lock(TimeSpan.FromSeconds(1)), JbRunners.Yield(), Logs.Capturing(logs));

        // Act
        await service.RunAsync(_config, Ct);

        // Assert
        LogEntry reported = logs.WithProperty("Dropped").ShouldHaveSingleItem();
        reported.Level.ShouldBe(LogLevel.Information);
        reported.Property("SolutionPath").ShouldBe(_config.SolutionPath);
        reported.Property("Dropped").ShouldBe(new List<string> { Path.GetFileName(ours) });
        reported.Property("LeftAloneCount").ShouldBe(1);
        reported.Property("FailureCount").ShouldBe(0);
        reported.Property("SolutionFileExists").ShouldBe(true);
    }

    [Fact]
    public async Task RunAsync_ReclaimingARemovedCheckout_SaysSoOnTheSameLine()
    {
        // Arrange — read back later, a reclaim and an ordinary reset are the same delete against different
        // futures: one explains why the next call is slow, the other says there is no next call.
        CapturingLoggerProvider logs = new();
        ResolvedConfig removed = RemovedCheckout();
        CacheHomes.PlantGenerationFor(_cacheHome, removed.SolutionPath);

        CacheResetService service = JbRunners.Reset(
            JbRunners.Lock(TimeSpan.FromSeconds(1)), JbRunners.Yield(), Logs.Capturing(logs));

        // Act
        await service.RunAsync(removed, Ct);

        // Assert
        LogEntry reported = logs.WithProperty("Dropped").ShouldHaveSingleItem();
        reported.Property("SolutionFileExists").ShouldBe(false);
        reported.Property("Consequence")
            .ShouldBe("the solution file does not exist, so this reclaimed the cache of a removed checkout");
    }

    /// <summary>The generation names alone, for the assertions that are about the split rather than whose.</summary>
    private static IReadOnlyList<string> Names(IEnumerable<LeftAloneGeneration> generations)
    {
        return generations.Select(generation => generation.Name).ToList();
    }

    /// <summary>
    ///     The config a reclaim is called with: a solution path with no file on it, whose cache generation
    ///     outlived the worktree, clone or copy deleted from it.
    /// </summary>
    private ResolvedConfig RemovedCheckout()
    {
        return Configs.Bare(_environment.CreateSolutionPath("App.sln"), _cacheHome);
    }

    private ResolvedConfig ConfigFor(string solutionFileName, string cacheHome)
    {
        string solutionPath = Path.Combine(_environment.CurrentDirectory, solutionFileName);
        File.WriteAllText(solutionPath, string.Empty);

        return Configs.Bare(solutionPath, cacheHome);
    }
}