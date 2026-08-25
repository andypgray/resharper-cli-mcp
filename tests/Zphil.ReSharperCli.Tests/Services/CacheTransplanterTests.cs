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
///     <see cref="CacheTransplanter" /> copies one solution's ReSharper cache under another's name, which is
///     both the only thing in this server that writes into a cache generation and an optimisation nobody
///     asked for. These tests run against real temp cache homes because the whole behaviour is filesystem
///     shape, and they pin it from both sides: it plants a faithful copy in the two situations it is for —
///     no generation at all, or one no successful run ever left a warm marker beside — and declines,
///     silently and leaving the cache home exactly as it found it, in every situation where it cannot prove
///     one of them holds. The second situation is a delete as well as a copy, so its order is pinned too:
///     nothing goes until the whole copy is standing beside it.
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
        JbWarmMarker.IsFreshWithin(config.SolutionPath, _cacheHome, TimeSpan.FromHours(1), NullLogger.Instance).ShouldBeFalse();
    }

    [Fact]
    public async Task TryTransplantAsync_GenerationWithAWarmMarker_LeavesItAlone()
    {
        // Arrange — a marker means a jb run against this path finished, so whatever is on disk is the
        // solution's own analysis. Replacing that would be a delete this server was not asked for, and
        // resetting first is how a caller asks for exactly that.
        CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        string existing = CacheHomes.PlantWarmDonor(_cacheHome, _worktreeSolution);
        File.WriteAllText(Path.Combine(existing, "Db", "CURRENT"), "this solution's own");

        // Act
        bool seeded = await Transplanter().TryTransplantAsync(ConfigFor(_worktreeSolution), Ct);

        // Assert
        seeded.ShouldBeFalse();
        File.ReadAllText(Path.Combine(existing, "Db", "CURRENT")).ShouldBe("this solution's own");
    }

    [Fact]
    public async Task TryTransplantAsync_UnmarkedHuskFromAKilledRun_IsReplacedByACopyOfTheDonor()
    {
        // Arrange — the situation this exists for, and the one the field found: a first run on a new checkout
        // died at the cap, leaving a part-built generation no marker vouches for. Left alone it would decline
        // for ever, because the remnant it was meant to be seeded over is itself what blocks the seeding.
        string donor = CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        File.WriteAllText(Path.Combine(donor, "Db", "000001.log"), "leveldb");
        string husk = CacheHomes.PlantGenerationFor(_cacheHome, _worktreeSolution);
        File.WriteAllText(Path.Combine(husk, "Db", "CURRENT"), "part-built");

        // Act
        bool seeded = await Transplanter().TryTransplantAsync(ConfigFor(_worktreeSolution), Ct);

        // Assert — the donor's content won, all of it, and the copy left nothing of itself behind.
        seeded.ShouldBeTrue();
        string target = TargetPath();
        File.ReadAllText(Path.Combine(target, "Db", "CURRENT")).ShouldBe("cache");
        File.ReadAllText(Path.Combine(target, "Db", "000001.log")).ShouldBe("leveldb");
        Directory.Exists(donor).ShouldBeTrue();
        Directory.EnumerateDirectories(_cacheHome, "*.transplanting").ShouldBeEmpty();
    }

    [Fact]
    public async Task TryTransplantAsync_GenerationWithAnEmptyLegacyMarker_LeavesItAlone()
    {
        // Arrange — a marker from an older build of this server, or one written under naming drift: it names
        // nothing, and it is still the record of a run that succeeded. Existence protects, not content, so
        // this server reading a marker it cannot use never turns into a delete.
        CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        string existing = CacheHomes.PlantGenerationFor(_cacheHome, _worktreeSolution);
        File.WriteAllText(Path.Combine(existing, "Db", "CURRENT"), "warmed by an older build");
        await File.WriteAllTextAsync(JbWarmMarker.PathFor(_worktreeSolution, _cacheHome), string.Empty, Ct);

        // Act
        bool seeded = await Transplanter().TryTransplantAsync(ConfigFor(_worktreeSolution), Ct);

        // Assert
        seeded.ShouldBeFalse();
        File.ReadAllText(Path.Combine(existing, "Db", "CURRENT")).ShouldBe("warmed by an older build");
    }

    [Fact]
    public async Task TryTransplantAsync_HuskLeftAfterACacheReset_StillDeclines()
    {
        // Arrange — a reset clears the marker, so the generation a run starts building afterwards and dies
        // part way through looks exactly like the husk above. The tombstone is what tells them apart, and it
        // outranks the replacement path: the user asked for cold.
        CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        ResolvedConfig config = ConfigFor(_worktreeSolution);
        CacheResetService reset = JbRunners.Reset(JbRunners.Lock(TimeSpan.FromSeconds(1)), JbRunners.Yield());
        await reset.RunAsync(config, Ct);
        string husk = CacheHomes.PlantGenerationFor(_cacheHome, _worktreeSolution);
        File.WriteAllText(Path.Combine(husk, "Db", "CURRENT"), "part-built since the reset");

        // Act
        bool seeded = await Transplanter().TryTransplantAsync(config, Ct);

        // Assert
        seeded.ShouldBeFalse();
        File.ReadAllText(Path.Combine(husk, "Db", "CURRENT")).ShouldBe("part-built since the reset");
    }

    [Fact]
    public async Task TryTransplantAsync_AfterACacheReset_DeclinesRatherThanUndoingIt()
    {
        // Arrange — the guardrail, through the real reset rather than a hand-written record: a user who asked
        // for a cold rebuild must not be handed a copy of the sibling index instead. The reset leaves no
        // generation at all, so the tombstone is the only thing declining here.
        CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        ResolvedConfig config = ConfigFor(_worktreeSolution);
        CacheHomes.PlantGenerationFor(_cacheHome, _worktreeSolution);
        CacheResetService reset = JbRunners.Reset(JbRunners.Lock(TimeSpan.FromSeconds(1)), JbRunners.Yield());
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
    public void DefaultDonorLockPatience_OutlastsTheReapOfARunTheCallerItselfKilled()
    {
        // The case above with the holder being the caller's own doing, and the one a timing test would only
        // catch by luck: a foreground caller cancels the pre-warm before queueing for its lease, and across
        // two solutions that lease is uncontended and granted at once — so it can reach the donor while the
        // pass it just killed still holds it. That lease drops only once the killed tree has been reaped, so
        // a patience shorter than the reap budget silently declines the donor and takes the cold run the
        // seeding exists to avoid. Pinned as the relationship between the two constants, because that is the
        // invariant; a duration assertion would restate one of them and say nothing about why.
        CacheTransplanter.DefaultDonorLockPatience.ShouldBeGreaterThanOrEqualTo(ProcessRunner.KilledTreeReapBudget);
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
        Task<bool> transplant = Task.Run(
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
    public async Task TryTransplantAsync_CancelledMidCopyOverAHusk_LeavesTheHuskUntouched()
    {
        // Arrange — the order pinned from the direction that matters most. A foreground call reclaiming the
        // generation lands mid-copy by design, and what it reclaims must still be the part-built remnant it
        // was going to resume. Enough files that the copy is still running when the cancel arrives.
        string donor = CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        for (var index = 0; index < 2000; index++)
            await File.WriteAllTextAsync(Path.Combine(donor, "Db", $"{index:D5}.ldb"), "x", Ct);

        string husk = CacheHomes.PlantGenerationFor(_cacheHome, _worktreeSolution);
        await File.WriteAllTextAsync(Path.Combine(husk, "Db", "CURRENT"), "part-built", Ct);

        string inProgress = InProgressPath();
        using CancellationTokenSource cancelling = new();

        // The token rather than its source, for the reason given in the test above.
        CancellationToken reclaimed = cancelling.Token;
        Task<bool> transplant = Task.Run(
            () => Transplanter().TryTransplantAsync(ConfigFor(_worktreeSolution), reclaimed), Ct);

        // Act — as soon as there is a partial copy to abandon.
        while (!Directory.Exists(inProgress) && !transplant.IsCompleted) await Task.Delay(1, Ct);
        await cancelling.CancelAsync();

        // Assert — the remnant is exactly as it was, because the delete only ever happens after the whole
        // copy is standing beside it. Cancellation can cost the copy and nothing else.
        await Should.ThrowAsync<OperationCanceledException>(() => transplant);
        File.ReadAllText(Path.Combine(husk, "Db", "CURRENT")).ShouldBe("part-built");
        Directory.Exists(inProgress).ShouldBeFalse();
    }

    [Fact]
    public async Task TryTransplantAsync_CopyThatFailsBeforeCompleting_LeavesTheHuskUntouched()
    {
        // Arrange — the same order without the race: a *file* where the copy gets built cannot be created
        // over, so the copy fails while the remnant is still the only thing on disk.
        CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        string husk = CacheHomes.PlantGenerationFor(_cacheHome, _worktreeSolution);
        await File.WriteAllTextAsync(Path.Combine(husk, "Db", "CURRENT"), "part-built", Ct);
        await File.WriteAllTextAsync(InProgressPath(), string.Empty, Ct);

        // Act
        bool seeded = await Transplanter().TryTransplantAsync(ConfigFor(_worktreeSolution), Ct);

        // Assert — declined, and the run about to start still has the remnant to resume.
        seeded.ShouldBeFalse();
        File.ReadAllText(Path.Combine(husk, "Db", "CURRENT")).ShouldBe("part-built");
    }

    [Fact]
    public async Task TryTransplantAsync_HuskThatCannotBeDeleted_DeclinesAndDiscardsTheCopy()
    {
        // Arrange — a jb this server knows nothing about holds the remnant open. The copy is finished and
        // the slot will not clear, which is the one new way this can fail.
        CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        string husk = CacheHomes.PlantGenerationFor(_cacheHome, _worktreeSolution);
        await File.WriteAllTextAsync(Path.Combine(husk, "Db", "CURRENT"), "part-built", Ct);

        bool seeded;
        using (CacheHomes.BlockDeletionOf(husk))
        {
            // Act
            seeded = await Transplanter().TryTransplantAsync(ConfigFor(_worktreeSolution), Ct);
        }

        // Assert — read once the hold is released, since on Windows it is the file itself being held. The
        // remnant survives whole, and the copy that could not land does not outlive the attempt.
        seeded.ShouldBeFalse();
        File.ReadAllText(Path.Combine(husk, "Db", "CURRENT")).ShouldBe("part-built");
        Directory.Exists(InProgressPath()).ShouldBeFalse();
    }

    [Fact]
    public async Task TryTransplantAsync_HuskWithAColdFork_SeedsTheSlotAndSweepsTheFork()
    {
        // Arrange — a second jb that could not open the remnant forked ".01" off it, so the run that never
        // finished left two directories. Both are this solution's, and no marker vouches for either.
        CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        string husk = CacheHomes.PlantGenerationFor(_cacheHome, _worktreeSolution);
        string fork = CacheHomes.PlantFork(_cacheHome, husk);

        // Act
        bool seeded = await Transplanter().TryTransplantAsync(ConfigFor(_worktreeSolution), Ct);

        // Assert — the copy lands in the generation jb opens, and the fork goes with the remnant it was
        // forked from rather than being left to describe a cache that no longer exists.
        seeded.ShouldBeTrue();
        File.ReadAllText(Path.Combine(TargetPath(), "Db", "CURRENT")).ShouldBe("cache");
        Directory.Exists(fork).ShouldBeFalse();
    }

    [Fact]
    public async Task TryTransplantAsync_ForkThatCannotBeDeleted_StillSeedsAndLeavesTheForkForAReset()
    {
        // Arrange — the sweep is the one delete allowed to fail, because it happens after the seeding has
        // landed. A fork something else is holding must cost disk rather than the copy.
        CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        string husk = CacheHomes.PlantGenerationFor(_cacheHome, _worktreeSolution);
        string fork = CacheHomes.PlantFork(_cacheHome, husk);

        bool seeded;
        using (CacheHomes.BlockDeletionOf(fork))
        {
            // Act
            seeded = await Transplanter().TryTransplantAsync(ConfigFor(_worktreeSolution), Ct);
        }

        // Assert — seeded regardless, with the fork left where a cache reset reclaims it.
        seeded.ShouldBeTrue();
        File.ReadAllText(Path.Combine(TargetPath(), "Db", "CURRENT")).ShouldBe("cache");
        Directory.Exists(fork).ShouldBeTrue();
    }

    [Fact]
    public async Task TryTransplantAsync_ReplacingAHusk_DoesNotClaimTheCopyIsWarm()
    {
        // Arrange — the marker is now also what protects a generation from being replaced, so stamping one
        // for an unvalidated copy would do more than advertise a donor: this path would fire once over a
        // remnant and then be locked out of it for ever.
        CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        CacheHomes.PlantGenerationFor(_cacheHome, _worktreeSolution);
        ResolvedConfig config = ConfigFor(_worktreeSolution);

        // Act
        (await Transplanter().TryTransplantAsync(config, Ct)).ShouldBeTrue();

        // Assert — from both sides of the marker: nothing to protect the copy, and nothing to debounce a
        // pre-warm with. Only jb exiting cleanly writes either.
        JbWarmMarker.Exists(config.SolutionPath, _cacheHome, NullLogger.Instance).ShouldBeFalse();
        JbWarmMarker.IsFreshWithin(config.SolutionPath, _cacheHome, TimeSpan.FromHours(1), NullLogger.Instance).ShouldBeFalse();
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

    /// <summary>
    ///     Every decline says why, and a decline that leaves the target <em>cold</em> says it at
    ///     <see cref="LogLevel.Information" />.
    /// </summary>
    /// <remarks>
    ///     These were five silent <c>return false</c> paths, which made "declined to seed" and "never looked"
    ///     one observation — and the difference between them is the whole diagnosis when a fresh checkout that
    ///     should have been seeded runs cold instead, which is the shape a week of field logs showed. The
    ///     reason text is matched rather than a property name because the reason <em>is</em> the payload here;
    ///     the level is the part that carries policy.
    /// </remarks>
    [Fact]
    public async Task TryTransplantAsync_NoDonorAtAll_SaysSoAtInformationBecauseTheRunWillBeCold()
    {
        // Arrange — a cold solution with nothing beside it: the ordinary first checkout.
        CapturingLoggerProvider logs = new();

        // Act
        await Transplanter(logs).TryTransplantAsync(ConfigFor(_worktreeSolution), Ct);

        // Assert
        LogEntry decline = Decline(logs);
        decline.Level.ShouldBe(LogLevel.Information);
        decline.Property("DeclineReason").ShouldBe("no warm cache of another copy of this solution to copy");
    }

    [Fact]
    public async Task TryTransplantAsync_AfterAReset_SaysTheColdRunWasAsked_ForAtInformation()
    {
        // Arrange — a reset outranks seeding, and the run it makes cold is the run the user asked for.
        CapturingLoggerProvider logs = new();
        CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        JbColdTombstone.Write(_worktreeSolution, _cacheHome, NullLogger.Instance);

        // Act
        await Transplanter(logs).TryTransplantAsync(ConfigFor(_worktreeSolution), Ct);

        // Assert
        LogEntry decline = Decline(logs);
        decline.Level.ShouldBe(LogLevel.Information);
        decline.Property("DeclineReason").ShouldBe("its cache was reset on purpose");
    }

    [Fact]
    public async Task TryTransplantAsync_DonorBusyElsewhere_NamesTheDonorAtInformation()
    {
        // Arrange — another session is analysing the donor, so it must not be read mid-write.
        CapturingLoggerProvider logs = new();
        string donor = CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        await using FileStream held = CacheHomes.HoldLockFile(_cacheHome, _mainSolution);

        // Act
        await Transplanter(logs).TryTransplantAsync(ConfigFor(_worktreeSolution), Ct);

        // Assert
        LogEntry decline = Decline(logs);
        decline.Level.ShouldBe(LogLevel.Information);
        decline.Property("DeclineReason").ShouldBe($"the donor {Path.GetFileName(donor)} is in use elsewhere");
    }

    [Fact]
    public async Task TryTransplantAsync_TargetAlreadyWarm_DeclinesAtDebugBecauseNothingIsAboutToBeSlow()
    {
        // Arrange — the ordinary case on every call of every session. At Information it would drown the very
        // lines the level exists to make findable.
        CapturingLoggerProvider logs = new();
        CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        CacheHomes.PlantWarmDonor(_cacheHome, _worktreeSolution);

        // Act
        await Transplanter(logs).TryTransplantAsync(ConfigFor(_worktreeSolution), Ct);

        // Assert
        LogEntry decline = Decline(logs);
        decline.Level.ShouldBe(LogLevel.Debug);
        decline.Property("DeclineReason").ShouldBe("a run against it already succeeded");
    }

    [Fact]
    public async Task TryTransplantAsync_Seeding_ReportsWhatItCopiedAndHowLongItTook()
    {
        // Arrange — the numbers the seeded-run premium had to be reconstructed by hand from two tool-call
        // totals. Two files, so the count is not trivially whatever one directory holds.
        CapturingLoggerProvider logs = new();
        string donor = CacheHomes.PlantWarmDonor(_cacheHome, _mainSolution);
        File.WriteAllText(Path.Combine(donor, "Db", "000001.log"), "leveldb");

        // Act
        await Transplanter(logs).TryTransplantAsync(ConfigFor(_worktreeSolution), Ct);

        // Assert — 5 bytes of "cache" plus 7 of "leveldb", and a duration that was actually measured.
        LogEntry seeded = logs.WithProperty("DonorBytes").ShouldHaveSingleItem();
        seeded.Level.ShouldBe(LogLevel.Information);
        seeded.Property("DonorFiles").ShouldBe(2);
        seeded.Property("DonorBytes").ShouldBe(12L);
        seeded.Property("CopiedMs").ShouldNotBeNull();
    }

    /// <summary>The one decline line this transplant wrote, whatever level it chose.</summary>
    private static LogEntry Decline(CapturingLoggerProvider logs)
    {
        return logs.WithProperty("DeclineReason").ShouldHaveSingleItem();
    }

    private CacheTransplanter Transplanter(CapturingLoggerProvider? logs = null)
    {
        ILogger<CacheTransplanter> logger = logs is null
            ? NullLogger<CacheTransplanter>.Instance
            : Logs.Capturing(logs).CreateLogger<CacheTransplanter>();

        return new CacheTransplanter(JbRunners.Lock(TimeSpan.FromSeconds(1)), logger, ShortPatience);
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
            .Single(marker => JbWarmMarker.TryReadGenerationName(marker.MarkerPath, _cacheHome, NullLogger.Instance) == generationName)
            .MarkerPath;

        File.SetLastWriteTimeUtc(markerPath, DateTime.UtcNow - age);
    }
}