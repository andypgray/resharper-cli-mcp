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
///     and it leaves behind the record that keeps the next run cold.
/// </summary>
public sealed class CacheResetServiceTests : IDisposable
{
    private readonly string _cacheHome;
    private readonly ResolvedConfig _config;
    private readonly FakeEnvironment _environment = new();
    private readonly CacheResetService _service = new(new JbRunLock(TimeSpan.FromSeconds(1)), new JbRunYield());

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
        outcome.LeftAlone.ShouldBe([Path.GetFileName(theirs)]);
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
        outcome.LeftAlone.ShouldBe([Path.GetFileName(theirs)]);
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
        JbWarmMarker.IsFreshWithin(_config.SolutionPath, _cacheHome, TimeSpan.FromHours(1)).ShouldBeTrue();

        // Act
        await _service.RunAsync(_config, Ct);

        // Assert
        JbWarmMarker.IsFreshWithin(_config.SolutionPath, _cacheHome, TimeSpan.FromHours(1)).ShouldBeFalse();
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
        JbColdTombstone.Exists(_config.SolutionPath, _cacheHome).ShouldBeTrue();
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
        CacheResetService service = new(new JbRunLock(TimeSpan.FromMilliseconds(250)), new JbRunYield());

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => service.RunAsync(_config, Ct));

        // Assert — it queued on the run rather than deleting the cache underneath it, and gave up intact.
        exception.Message.ShouldContain("Another jb run already holds the ReSharper cache");
        Directory.Exists(ours).ShouldBeTrue();
    }

    /// <summary>
    ///     A config naming a solution in a directory of this test's own. Only the solution path and cache home
    ///     matter here: a reset runs no <c>jb</c>, so settings, extensions, and the profile play no part.
    /// </summary>
    private ResolvedConfig ConfigFor(string solutionFileName, string cacheHome)
    {
        string solutionPath = Path.Combine(_environment.CurrentDirectory, solutionFileName);
        File.WriteAllText(solutionPath, string.Empty);

        return Configs.Bare(solutionPath, cacheHome);
    }
}