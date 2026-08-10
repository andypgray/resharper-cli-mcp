using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;

namespace Zphil.ReSharperCli.Tests.Services;

/// <summary>
///     <see cref="CacheResetService" /> is the only tool path that deletes anything outside the files a
///     caller named, so these pin the three properties that make that safe: it drops exactly this solution's
///     generations, it refuses rather than guessing when the cache home cannot tell two solutions apart, and
///     it will not delete a cache generation while a <c>jb</c> run holds it.
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
        PlantGeneration(cacheHome, "_App.100200300.00");
        PlantGeneration(cacheHome, "_App.100200300.01");
        PlantGeneration(cacheHome, "_App.Core.400500600.00");
        PlantGeneration(cacheHome, "_Other.99.00");
        CacheResetService service = new(new JbRunLock(TimeSpan.FromSeconds(1)));

        // Act
        CacheResetOutcome outcome = await service.RunAsync(ConfigFor("App.sln", cacheHome), Ct);

        // Assert
        outcome.Dropped.ShouldBe(["_App.100200300.00", "_App.100200300.01"]);
        outcome.Failures.ShouldBeEmpty();
        Directory.Exists(Path.Combine(cacheHome, "_App.100200300.00")).ShouldBeFalse();
        Directory.Exists(Path.Combine(cacheHome, "_App.Core.400500600.00")).ShouldBeTrue();
        Directory.Exists(Path.Combine(cacheHome, "_Other.99.00")).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_DroppingAGeneration_ClearsTheWarmMarkerThatDescribedIt()
    {
        // Arrange — the marker says "a jb run against this generation succeeded recently", which is what stops
        // the next session pre-warming. Leaving it behind would make the next session's first real call pay
        // the cold run this reset just guaranteed.
        string cacheHome = _environment.CreateTempDirectory();
        ResolvedConfig config = ConfigFor("App.sln", cacheHome);
        PlantGeneration(cacheHome, "_App.123.00");
        JbWarmMarker.Stamp(config.SolutionPath, cacheHome);
        JbWarmMarker.IsFreshWithin(config.SolutionPath, cacheHome, TimeSpan.FromHours(1)).ShouldBeTrue();
        CacheResetService service = new(new JbRunLock(TimeSpan.FromSeconds(1)));

        // Act
        await service.RunAsync(config, Ct);

        // Assert
        JbWarmMarker.IsFreshWithin(config.SolutionPath, cacheHome, TimeSpan.FromHours(1)).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_NothingCachedForThisSolution_ReportsNothingRatherThanFailing()
    {
        // Arrange — an already-reset solution, or one that has never been analysed. The tool is idempotent, so
        // the second call in a row is a normal thing to do.
        string cacheHome = _environment.CreateTempDirectory();
        PlantGeneration(cacheHome, "_Other.99.00");
        CacheResetService service = new(new JbRunLock(TimeSpan.FromSeconds(1)));

        // Act
        CacheResetOutcome outcome = await service.RunAsync(ConfigFor("App.sln", cacheHome), Ct);

        // Assert
        outcome.Dropped.ShouldBeEmpty();
        outcome.Failures.ShouldBeEmpty();
        Directory.Exists(Path.Combine(cacheHome, "_Other.99.00")).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_TwoSolutionsSharingAFileName_RefusesAndNamesTheCandidates()
    {
        // Arrange — jb's directory name carries a hash of the solution path and not the path, so nothing here
        // can say which generation belongs to the bound solution. This call deletes what it picks, so it does
        // not pick.
        string cacheHome = _environment.CreateTempDirectory();
        PlantGeneration(cacheHome, "_App.1344362500.00");
        PlantGeneration(cacheHome, "_App.-1749040816.00");
        CacheResetService service = new(new JbRunLock(TimeSpan.FromSeconds(1)));

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => service.RunAsync(ConfigFor("App.sln", cacheHome), Ct));

        // Assert — the candidates are named, so the user can finish the job by hand, and nothing was deleted.
        exception.Message.ShouldContain("more than one solution file named \"App\"");
        exception.Message.ShouldContain("  - _App.1344362500.00");
        exception.Message.ShouldContain("  - _App.-1749040816.00");
        exception.Message.ShouldContain("JB_CACHE_HOME");
        Directory.Exists(Path.Combine(cacheHome, "_App.1344362500.00")).ShouldBeTrue();
        Directory.Exists(Path.Combine(cacheHome, "_App.-1749040816.00")).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_AJbRunHoldsTheCacheGeneration_WaitsAndThenRefusesWithoutDeleting()
    {
        // Arrange — the whole reason this is a server-side tool rather than advice to delete a glob. The held
        // lock file is what another session's live jb looks like from here.
        string cacheHome = _environment.CreateTempDirectory();
        ResolvedConfig config = ConfigFor("App.sln", cacheHome);
        PlantGeneration(cacheHome, "_App.123.00");

        string key = JbRunLock.ComputeKey(config.SolutionPath, cacheHome);
        await using FileStream held = new(
            JbRunLock.LockFilePathFor(cacheHome, key), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        CacheResetService service = new(new JbRunLock(TimeSpan.FromMilliseconds(250)));

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => service.RunAsync(config, Ct));

        // Assert — it queued on the run rather than deleting the cache underneath it, and gave up intact.
        exception.Message.ShouldContain("Another inspect or cleanup is already running");
        Directory.Exists(Path.Combine(cacheHome, "_App.123.00")).ShouldBeTrue();
    }

    /// <summary>
    ///     A config naming a solution in this test's own temp directory. Only the solution path and cache home
    ///     matter here: a reset runs no <c>jb</c>, so settings, extensions, and the profile play no part.
    /// </summary>
    private ResolvedConfig ConfigFor(string solutionFileName, string cacheHome)
    {
        string solutionPath = Path.Combine(_environment.CurrentDirectory, solutionFileName);
        File.WriteAllText(solutionPath, string.Empty);

        return new ResolvedConfig(solutionPath, null, null, cacheHome, null, null, "jb", ConfigWarnings.None);
    }

    private static void PlantGeneration(string cacheHome, string directoryName)
    {
        string path = Path.Combine(cacheHome, directoryName, "Db");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "CURRENT"), "cache");
    }
}