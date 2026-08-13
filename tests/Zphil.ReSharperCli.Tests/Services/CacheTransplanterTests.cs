using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Services;

/// <summary>
///     <see cref="CacheTransplanter" /> copies one solution's ReSharper cache under another's name, which is
///     both the only thing in this server that writes into a cache generation and an optimisation nobody
///     asked for. These tests run against real temp cache homes because the whole behaviour is filesystem
///     shape, and they pin it from both sides: it plants a faithful copy in the one situation it is for, and
///     declines — silently, leaving the cache home exactly as it found it — in every situation where it
///     cannot prove that situation holds.
/// </summary>
public sealed class CacheTransplanterTests : IDisposable
{
    private static readonly TimeSpan ShortPatience = TimeSpan.FromMilliseconds(250);

    private readonly string _cacheHome;
    private readonly FakeEnvironment _environment = new();

    /// <summary>The two checkouts of one repository this whole class is about: same file name, different paths.</summary>
    private readonly string _mainSolution;

    private readonly string _worktreeSolution;

    public CacheTransplanterTests()
    {
        _cacheHome = _environment.CreateTempDirectory();
        _mainSolution = _environment.CreateSolutionPath("App.sln");
        _worktreeSolution = _environment.CreateSolutionPath("App.sln");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public async Task TryTransplantAsync_ColdSolutionBesideAWarmSibling_PlantsAFaithfulCopyUnderItsOwnHash()
    {
        // Arrange — the whole point: a fresh worktree, and the main checkout's warm cache beside it.
        string donor = CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        File.WriteAllText(Path.Combine(donor, "Db", "000001.log"), "leveldb");

        // Act
        bool seeded = await Transplanter().TryTransplantAsync(ConfigFor(_worktreeSolution), Ct);

        // Assert — planted under the name jb will look for, byte for byte, with the donor untouched.
        seeded.ShouldBeTrue();
        string target = TargetPath();
        File.ReadAllText(Path.Combine(target, "Db", "CURRENT")).ShouldBe("cache");
        File.ReadAllText(Path.Combine(target, "Db", "000001.log")).ShouldBe("leveldb");
        Directory.Exists(donor).ShouldBeTrue();
        Directory.EnumerateDirectories(_cacheHome, "*.transplanting").ShouldBeEmpty();
    }

    [Fact]
    public async Task TryTransplantAsync_Seeding_DoesNotClaimTheCopyIsWarm()
    {
        // Arrange — the copy is unvalidated until jb opens it. Stamping the target's marker here would both
        // suppress a pre-warm and advertise the copy as a donor, on the strength of nothing having run.
        CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        ResolvedConfig config = ConfigFor(_worktreeSolution);

        // Act
        await Transplanter().TryTransplantAsync(config, Ct);

        // Assert
        JbWarmMarker.IsFreshWithin(config.SolutionPath, _cacheHome, TimeSpan.FromHours(1)).ShouldBeFalse();
    }

    [Fact]
    public async Task TryTransplantAsync_SolutionThatAlreadyHasAGeneration_LeavesItAlone()
    {
        // Arrange — even a stunted one, left by a run killed at the cap. Replacing it would be a delete this
        // server was not asked for, and resetting first is how a caller asks for exactly that.
        CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        string existing = CacheHomes.PlantGenerationFor(_cacheHome, _worktreeSolution);
        File.WriteAllText(Path.Combine(existing, "Db", "CURRENT"), "half-built");

        // Act
        bool seeded = await Transplanter().TryTransplantAsync(ConfigFor(_worktreeSolution), Ct);

        // Assert
        seeded.ShouldBeFalse();
        File.ReadAllText(Path.Combine(existing, "Db", "CURRENT")).ShouldBe("half-built");
    }

    [Fact]
    public async Task TryTransplantAsync_AfterACacheReset_DeclinesRatherThanUndoingIt()
    {
        // Arrange — the guardrail, through the real reset rather than a hand-written record: a user who asked
        // for a cold rebuild must not be handed a copy of the sibling index instead.
        CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        ResolvedConfig config = ConfigFor(_worktreeSolution);
        CacheHomes.PlantGenerationFor(_cacheHome, _worktreeSolution);
        CacheResetService reset = new(new JbRunLock(TimeSpan.FromSeconds(1)));
        await reset.RunAsync(config, Ct);

        // Act
        bool seeded = await Transplanter().TryTransplantAsync(config, Ct);

        // Assert
        seeded.ShouldBeFalse();
        Directory.Exists(TargetPath()).ShouldBeFalse();
    }

    [Fact]
    public async Task TryTransplantAsync_NothingElseInTheCacheHome_DeclinesQuietly()
    {
        // Arrange & Act — the first run on a machine. There is nothing to copy, and that is not a fault.
        bool seeded = await Transplanter().TryTransplantAsync(ConfigFor(_worktreeSolution), Ct);

        // Assert
        seeded.ShouldBeFalse();
        Directory.EnumerateDirectories(_cacheHome).ShouldBeEmpty();
    }

    [Fact]
    public async Task TryTransplantAsync_AGenerationWithNoWarmMarker_IsNotADonor()
    {
        // Arrange — a directory alone says a cache exists; only a marker says a run against it finished. The
        // difference is a usable index versus the husk of a run that was killed at the cap.
        CacheHomes.PlantGenerationFor(_cacheHome, _mainSolution);

        // Act & Assert
        (await Transplanter().TryTransplantAsync(ConfigFor(_worktreeSolution), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task TryTransplantAsync_MarkerWrittenBeforeItRecordedNames_IsNotADonor()
    {
        // Arrange — a marker from an older build of this server, or one written under naming drift: warm, and
        // silent about what it warmed. This is the self-disable, seen from the donor side.
        CacheHomes.PlantGenerationFor(_cacheHome, _mainSolution);
        File.WriteAllText(JbWarmMarker.PathFor(_mainSolution, _cacheHome), string.Empty);

        // Act & Assert
        (await Transplanter().TryTransplantAsync(ConfigFor(_worktreeSolution), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task TryTransplantAsync_MarkerNamingAGenerationThatIsGone_IsNotADonor()
    {
        // Arrange — the cache was cleared out from under the marker, by a reset elsewhere or by jb's own
        // collection of generations nothing has touched for a month.
        string donor = CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        Directory.Delete(donor, true);

        // Act & Assert
        (await Transplanter().TryTransplantAsync(ConfigFor(_worktreeSolution), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task TryTransplantAsync_AnotherSolutionEntirely_IsNotADonor()
    {
        // Arrange — a warm cache for a different solution in the same cache home. It shares nothing with this
        // one but the directory it lives in.
        CacheHomes.PlantWarmDonor(_cacheHome, _environment.CreateSolutionPath("Other.sln"));

        // Act & Assert
        (await Transplanter().TryTransplantAsync(ConfigFor(_worktreeSolution), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task TryTransplantAsync_TwoWarmSiblings_CopiesTheOneWarmedMostRecently()
    {
        // Arrange — several checkouts of one repository, analysed at different times. The freshest cache is
        // the closest to this worktree's code, so it is the one worth the copy.
        string stale = CacheHomes.PlantWarmDonor(_cacheHome, _environment.CreateSolutionPath("App.sln"));
        File.WriteAllText(Path.Combine(stale, "Db", "CURRENT"), "stale");
        string fresh = CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        File.WriteAllText(Path.Combine(fresh, "Db", "CURRENT"), "fresh");
        AgeMarker(stale, TimeSpan.FromHours(2));

        // Act
        await Transplanter().TryTransplantAsync(ConfigFor(_worktreeSolution), Ct);

        // Assert
        string target = TargetPath();
        File.ReadAllText(Path.Combine(target, "Db", "CURRENT")).ShouldBe("fresh");
    }

    [Fact]
    public async Task TryTransplantAsync_DonorHeldByALiveRun_DeclinesInsteadOfReadingItMidWrite()
    {
        // Arrange — the donor's lock file held by a live run. Copying a cache jb is writing would produce a
        // torn one, and the copy is worthless if it has to be wiped anyway.
        CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        await using FileStream held = CacheHomes.HoldLockFile(_cacheHome, _mainSolution);

        // Act
        bool seeded = await Transplanter().TryTransplantAsync(ConfigFor(_worktreeSolution), Ct);

        // Assert
        seeded.ShouldBeFalse();
        Directory.Exists(TargetPath()).ShouldBeFalse();
    }

    [Fact]
    public async Task TryTransplantAsync_CancelledPartWayThroughTheCopy_ThrowsAndLeavesNothingBehind()
    {
        // Arrange — cancellation is how a foreground call reclaims a cache generation, so it arrives mid-copy
        // by design rather than by accident. Enough files that the copy is still running when it lands.
        string donor = CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        for (var index = 0; index < 2000; index++)
            await File.WriteAllTextAsync(Path.Combine(donor, "Db", $"{index:D5}.ldb"), "x", Ct);

        string inProgress = InProgressPath();
        using CancellationTokenSource cancelling = new();

        // The token rather than its source: a struct copy, so the running copy cannot end up reading a source
        // this method has already disposed on its way out.
        CancellationToken reclaimed = cancelling.Token;
        var transplant = Task.Run(
            () => Transplanter().TryTransplantAsync(ConfigFor(_worktreeSolution), reclaimed), Ct);

        // Act — as soon as there is a partial copy to abandon.
        while (!Directory.Exists(inProgress) && !transplant.IsCompleted) await Task.Delay(1, Ct);
        await cancelling.CancelAsync();

        // Assert — cancellation is the one thing this reports by throwing, and it takes the partial copy with
        // it: neither a generation jb could open nor a stray tree nothing would ever clean up.
        await Should.ThrowAsync<OperationCanceledException>(() => transplant);
        Directory.Exists(TargetPath()).ShouldBeFalse();
        Directory.Exists(inProgress).ShouldBeFalse();
    }

    [Fact]
    public async Task TryTransplantAsync_CopyThatCannotBeMovedIntoPlace_CleansUpAfterItselfWithoutThrowing()
    {
        // Arrange — a *file* occupying the target generation's name, which the directory-shaped parsers here
        // do not see and the final move cannot survive. Stands in for every way the copy can fail late.
        CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        await File.WriteAllTextAsync(TargetPath(), string.Empty, Ct);

        // Act
        bool seeded = await Transplanter().TryTransplantAsync(ConfigFor(_worktreeSolution), Ct);

        // Assert — the call it was speeding up carries on, and the half-made copy does not outlive it.
        seeded.ShouldBeFalse();
        Directory.Exists(InProgressPath()).ShouldBeFalse();
    }

    [Fact]
    public async Task TryTransplantAsync_LeftoverPartialCopyFromAKilledProcess_IsReplacedRatherThanTrippedOver()
    {
        // Arrange — a server killed mid-copy leaves this behind. It is invisible to every parser here, so
        // nothing else will ever clean it up, and a copy that could not start over it would never recover.
        CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        string leftover = InProgressPath();
        Directory.CreateDirectory(Path.Combine(leftover, "Db"));
        File.WriteAllText(Path.Combine(leftover, "Db", "CURRENT"), "abandoned");

        // Act
        bool seeded = await Transplanter().TryTransplantAsync(ConfigFor(_worktreeSolution), Ct);

        // Assert
        seeded.ShouldBeTrue();
        Directory.Exists(leftover).ShouldBeFalse();
        string target = TargetPath();
        File.ReadAllText(Path.Combine(target, "Db", "CURRENT")).ShouldBe("cache");
    }

    [Fact]
    public async Task TryTransplantAsync_CacheHomeThatCannotBeRead_DeclinesInsteadOfFailingTheCall()
    {
        // Arrange — a cache home no path API will accept. This runs on the way into a call the user made, and
        // has no claim on failing one.
        ResolvedConfig config = ConfigFor(_worktreeSolution) with { CacheHome = _cacheHome + "\0invalid" };

        // Act & Assert
        (await Transplanter().TryTransplantAsync(config, Ct)).ShouldBeFalse();
    }

    private CacheTransplanter Transplanter()
    {
        return new CacheTransplanter(new JbRunLock(TimeSpan.FromSeconds(1)), ShortPatience);
    }

    /// <summary>Where a seeded generation for the worktree lands, and where it is built before it lands.</summary>
    private string TargetPath()
    {
        return CacheHomes.GenerationPathFor(_cacheHome, _worktreeSolution);
    }

    private string InProgressPath()
    {
        return TargetPath() + ".transplanting";
    }

    private ResolvedConfig ConfigFor(string solutionPath)
    {
        return Configs.Bare(solutionPath, _cacheHome);
    }

    /// <summary>Back-date the warm marker that names <paramref name="generationPath" />.</summary>
    private void AgeMarker(string generationPath, TimeSpan age)
    {
        string generationName = Path.GetFileName(generationPath);
        string markerPath = JbWarmMarker.FindAll(_cacheHome)
            .Single(marker => JbWarmMarker.TryReadGenerationName(marker.MarkerPath, _cacheHome) == generationName)
            .MarkerPath;

        File.SetLastWriteTimeUtc(markerPath, DateTime.UtcNow - age);
    }
}