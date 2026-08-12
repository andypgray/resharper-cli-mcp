using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Services;

/// <summary>
///     <see cref="CacheResetService" /> is the only tool path that deletes anything outside the files a
///     caller named, so these pin the properties that make that safe: it drops exactly the generations whose
///     names carry this solution path's own hash, it reports rather than touches the ones that do not, it
///     will not delete a cache generation while a <c>jb</c> run holds it, and it leaves behind the record
///     that keeps the next run cold.
/// </summary>
public sealed class CacheResetServiceTests : IDisposable
{
    private readonly FakeEnvironment _environment = new();

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
        string cacheHome = _environment.CreateTempDirectory();
        ResolvedConfig config = ConfigFor("App.sln", cacheHome);
        string ours = CacheHomes.PlantGenerationFor(cacheHome, config.SolutionPath);
        string fork = CacheHomes.PlantGeneration(cacheHome, ForkOf(ours));
        CacheHomes.PlantGeneration(cacheHome, "_App.Core.400500600.00");
        CacheHomes.PlantGeneration(cacheHome, "_Other.99.00");
        CacheResetService service = new(new JbRunLock(TimeSpan.FromSeconds(1)));

        // Act
        CacheResetOutcome outcome = await service.RunAsync(config, Ct);

        // Assert
        outcome.Dropped.ShouldBe([Path.GetFileName(ours), Path.GetFileName(fork)]);
        outcome.LeftAlone.ShouldBeEmpty();
        outcome.Failures.ShouldBeEmpty();
        Directory.Exists(ours).ShouldBeFalse();
        Directory.Exists(fork).ShouldBeFalse();
        Directory.Exists(Path.Combine(cacheHome, "_App.Core.400500600.00")).ShouldBeTrue();
        Directory.Exists(Path.Combine(cacheHome, "_Other.99.00")).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_AnotherCheckoutsGenerationBesideOurs_DropsOnlyOursAndSaysSo()
    {
        // Arrange — two checkouts of one repository sharing a cache home: same solution file name, different
        // paths, so jb hashed them apart. Reproducing that hash is what turns this from an ambiguity the tool
        // used to refuse on into an ordinary answer.
        string cacheHome = _environment.CreateTempDirectory();
        ResolvedConfig config = ConfigFor("App.sln", cacheHome);
        string ours = CacheHomes.PlantGenerationFor(cacheHome, config.SolutionPath);
        string theirs = CacheHomes.PlantGenerationFor(cacheHome, Path.Combine(_environment.CreateTempDirectory(), "App.sln"));
        CacheResetService service = new(new JbRunLock(TimeSpan.FromSeconds(1)));

        // Act
        CacheResetOutcome outcome = await service.RunAsync(config, Ct);

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
        string cacheHome = _environment.CreateTempDirectory();
        ResolvedConfig config = ConfigFor("App.sln", cacheHome);
        string theirs = CacheHomes.PlantGenerationFor(cacheHome, Path.Combine(_environment.CreateTempDirectory(), "App.sln"));
        CacheResetService service = new(new JbRunLock(TimeSpan.FromSeconds(1)));

        // Act
        CacheResetOutcome outcome = await service.RunAsync(config, Ct);

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
        string cacheHome = _environment.CreateTempDirectory();
        ResolvedConfig config = ConfigFor("App.sln", cacheHome);
        CacheHomes.PlantWarmDonor(cacheHome, config.SolutionPath);
        JbWarmMarker.IsFreshWithin(config.SolutionPath, cacheHome, TimeSpan.FromHours(1)).ShouldBeTrue();
        CacheResetService service = new(new JbRunLock(TimeSpan.FromSeconds(1)));

        // Act
        await service.RunAsync(config, Ct);

        // Assert
        JbWarmMarker.IsFreshWithin(config.SolutionPath, cacheHome, TimeSpan.FromHours(1)).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_EvenWithNothingToDrop_RecordsThatTheNextRunIsMeantToBeCold()
    {
        // Arrange — an already-reset solution. "Nothing was deleted" is not the same as "no reset happened":
        // the caller asked for cold, and the record is what stops a later run seeding this cache from a
        // sibling checkout instead of rebuilding it.
        string cacheHome = _environment.CreateTempDirectory();
        ResolvedConfig config = ConfigFor("App.sln", cacheHome);
        CacheResetService service = new(new JbRunLock(TimeSpan.FromSeconds(1)));

        // Act
        CacheResetOutcome outcome = await service.RunAsync(config, Ct);

        // Assert
        outcome.Dropped.ShouldBeEmpty();
        JbColdTombstone.Exists(config.SolutionPath, cacheHome).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_NothingCachedForThisSolution_ReportsNothingRatherThanFailing()
    {
        // Arrange — an already-reset solution, or one that has never been analysed. The tool is idempotent, so
        // the second call in a row is a normal thing to do.
        string cacheHome = _environment.CreateTempDirectory();
        CacheHomes.PlantGeneration(cacheHome, "_Other.99.00");
        CacheResetService service = new(new JbRunLock(TimeSpan.FromSeconds(1)));

        // Act
        CacheResetOutcome outcome = await service.RunAsync(ConfigFor("App.sln", cacheHome), Ct);

        // Assert
        outcome.Dropped.ShouldBeEmpty();
        outcome.LeftAlone.ShouldBeEmpty();
        outcome.Failures.ShouldBeEmpty();
        Directory.Exists(Path.Combine(cacheHome, "_Other.99.00")).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_AJbRunHoldsTheCacheGeneration_WaitsAndThenRefusesWithoutDeleting()
    {
        // Arrange — the whole reason this is a server-side tool rather than advice to delete a glob. The held
        // lock file is what another session's live jb looks like from here.
        string cacheHome = _environment.CreateTempDirectory();
        ResolvedConfig config = ConfigFor("App.sln", cacheHome);
        string ours = CacheHomes.PlantGenerationFor(cacheHome, config.SolutionPath);

        string key = JbRunLock.ComputeKey(config.SolutionPath, cacheHome);
        await using FileStream held = new(
            JbRunLock.LockFilePathFor(cacheHome, key), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        CacheResetService service = new(new JbRunLock(TimeSpan.FromMilliseconds(250)));

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => service.RunAsync(config, Ct));

        // Assert — it queued on the run rather than deleting the cache underneath it, and gave up intact.
        exception.Message.ShouldContain("Another inspect or cleanup is already running");
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

        return new ResolvedConfig(solutionPath, null, null, cacheHome, null, null, "jb", ConfigWarnings.None);
    }

    /// <summary>The name of the <c>.01</c> generation a concurrent <c>jb</c> forks off <paramref name="generation" />.</summary>
    private static string ForkOf(string generation)
    {
        return Path.GetFileName(generation).Replace(".00", ".01");
    }
}