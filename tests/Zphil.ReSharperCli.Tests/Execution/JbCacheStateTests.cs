using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     <see cref="JbCacheState.Summary" /> is one sentence read by three surfaces — the opening log line, the
///     progress message sent while <c>jb</c> loads the solution, and, through
///     <see cref="JbCacheState.CostBand" />, the timeout message — so its arms are pinned here rather than
///     inferred from any one of them. Two claims carry the class. Every arm reads exactly as it always has
///     when no figure is recorded, which is what lets a feature be added to this sentence without touching a
///     single consumer's expectations. And a figure appears only where one is comparable: the arms with no
///     band quote nothing, however they are constructed.
/// </summary>
public sealed class JbCacheStateTests
{
    private const string Generation = "_App.123.00";

    /// <summary>The two builds of the measured patch bump: one wrote the cache, the other is about to open it.</summary>
    private const string ThisJb = "2026.2.1";

    private const string AnotherJb = "2026.2.0.2";

    private static readonly TimeSpan MarkerAge = TimeSpan.FromMinutes(14);

    [Fact]
    public void Summary_EveryArmWithNoFigure_ReadsExactlyAsItAlwaysHas()
    {
        // Assert — the regression guard, and the reason it is one assertion rather than six: these six
        // strings are what every existing consumer of this sentence was written against, so an arm tidied
        // in passing breaks a log line and a progress message that nothing else pins together.
        Unreadable().Summary.ShouldBe("cache state unreadable");
        Cold().Summary.ShouldBe("cold (none on disk)");
        ColdAfterAReset().Summary.ShouldBe("cold after a reset (none on disk)");
        Seeded().Summary.ShouldBe($"seeded from a sibling checkout ({Generation}), and this run re-keys it");
        PartBuilt().Summary.ShouldBe($"part-built ({Generation}, no warm marker — a run against it was killed)");
        Warm().Summary.ShouldBe($"warm (14m old marker, {Generation})");
    }

    [Fact]
    public void Summary_AColdRunWithARecordedCost_NamesWhatTheLastColdRunTook()
    {
        // Assert — the case the feature exists for. A cold run is minutes of silence, and the figure is what
        // separates "slow, as expected" from "stuck".
        JbCacheState state = Cold() with { LastComparableCost = TimeSpan.FromSeconds(497) };

        state.Summary.ShouldBe("cold (none on disk; the last cold run took 8 minutes 17 seconds)");
    }

    [Fact]
    public void Summary_ASeededRunWithARecordedCost_QuotesTheSeededFigureInsideTheParenthetical()
    {
        // Assert — and the clause that follows the parenthetical stays where it is, since the figure
        // describes the cache rather than the re-keying.
        JbCacheState state = Seeded() with { LastComparableCost = TimeSpan.FromSeconds(456) };

        state.Summary.ShouldBe(
            $"seeded from a sibling checkout ({Generation}; the last seeded run took 7 minutes 36 seconds), "
            + "and this run re-keys it");
    }

    [Fact]
    public void Summary_AWarmRunWithARecordedCost_QuotesTheWarmFigureAndNotAnother()
    {
        // Assert — the band is the whole point of the record: measured on one solution, a warm run took 39
        // seconds where the seeded run before it took 456, so a single remembered number would have told
        // this caller to expect seven minutes.
        JbCacheState state = Warm() with { LastComparableCost = TimeSpan.FromSeconds(39) };

        state.Summary.ShouldBe($"warm (14m old marker, {Generation}; the last warm run took 39 seconds)");
    }

    [Fact]
    public void Summary_AGenerationAnotherJbBuildWrote_ReadsAsStaleAndNamesBothBuilds()
    {
        // Assert — the lie this arm exists to stop. Measured across one patch bump on a static tree: the run
        // after the upgrade took 220 seconds where its own second run took 64, so a generation an earlier
        // build left behind buys the next run nothing and the sentence has to say which build wrote it.
        Stale().Summary.ShouldBe($"stale (cache written by jb {AnotherJb}, this is {ThisJb}, and jb rebuilds it)");
    }

    [Fact]
    public void Summary_AMarkerNamingNoJbBuild_ReadsAsStaleWithoutInventingOne()
    {
        // Assert — every marker written before the build was recorded, which is every marker on disk the
        // first time a server carrying this reads one. It cannot be vouched for, so it reads as the state
        // that costs work rather than the one that promises none.
        StaleWithNoRecordedBuild().Summary.ShouldBe($"stale (cache written by an earlier jb, this is {ThisJb}, and jb rebuilds it)");
    }

    [Fact]
    public void Summary_AGenerationThisJbBuildWrote_ReadsWarmExactlyAsItAlwaysHas()
    {
        // Assert — the other side of the same judgement, and the one that must not have moved: a marker this
        // build wrote is the ordinary warm cache, spelled the way every consumer already expects.
        WarmUnderThisJb().Summary.ShouldBe($"warm (14m old marker, {Generation})");
    }

    [Fact]
    public void Summary_NoCurrentJbBuildKnown_IgnoresTheMarkersAndReadsWarm()
    {
        // Assert — the off switch. A caller that cannot name its own jb has nothing to compare against, and a
        // server in that position calling every cache stale for ever would be worse than saying nothing.
        JbCacheState state = Warm() with { MarkerJbVersion = AnotherJb };

        state.Summary.ShouldBe($"warm (14m old marker, {Generation})");
    }

    [Fact]
    public void Summary_AStaleCacheWithARecordedCost_QuotesTheColdFigureAndNotTheWarmOne()
    {
        // Assert — the band and the arm are one decision. jb rebuilds this generation in place, so what the
        // run is about to cost is what a cold one cost; quoting the warm figure here is exactly the minute
        // this arm was added to stop being promised.
        JbCacheState state = Stale() with { LastComparableCost = TimeSpan.FromSeconds(116) };

        state.Summary.ShouldBe(
            $"stale (cache written by jb {AnotherJb}, this is {ThisJb}, and jb rebuilds it; "
            + "the last cold run took 1 minute 56 seconds)");
    }

    [Fact]
    public void Summary_PartBuiltHandedAFigure_StillQuotesNothing()
    {
        // Assert — a part-built generation is the remnant of a run that was killed, and how much of the work
        // survived depends on when it died. Two resumptions of differently killed runs are not comparable, so
        // this arm may not quote even when a figure is put in front of it.
        JbCacheState state = PartBuilt() with { LastComparableCost = TimeSpan.FromSeconds(300) };

        state.Summary.ShouldBe($"part-built ({Generation}, no warm marker — a run against it was killed)");
    }

    [Fact]
    public void Summary_AnUnreadableCacheHandedAFigure_StillQuotesNothing()
    {
        // Assert — the reading failed, so there is nothing to say a figure would be comparable to.
        JbCacheState state = Unreadable() with { LastComparableCost = TimeSpan.FromSeconds(300) };

        state.Summary.ShouldBe("cache state unreadable");
    }

    [Theory]
    [InlineData("unreadable", null)]
    [InlineData("cold", "cold")]
    [InlineData("cold after a reset", "cold")]
    [InlineData("seeded", "seeded")]
    [InlineData("part-built", null)]
    [InlineData("stale", "cold")]
    [InlineData("stale, no build recorded", "cold")]
    [InlineData("warm", "warm")]
    public void CostBand_MirrorsTheSummaryArms(string arm, string? expected)
    {
        // Assert — one arm, one band, in the order the summary decides them. A state the summary describes in
        // its own words is a state whose duration only runs described the same way predict, and a reset makes
        // no difference to that: a cold cache is a cold cache however it came to be empty.
        JbCacheState state = ArmNamed(arm);

        string? band = state.CostBand is { } value ? JbCostRecord.Label(value) : null;

        band.ShouldBe(expected);
    }

    private static JbCacheState ArmNamed(string arm)
    {
        return arm switch
        {
            "unreadable" => Unreadable(),
            "cold" => Cold(),
            "cold after a reset" => ColdAfterAReset(),
            "seeded" => Seeded(),
            "part-built" => PartBuilt(),
            "stale" => Stale(),
            "stale, no build recorded" => StaleWithNoRecordedBuild(),
            "warm" => Warm(),
            _ => throw new ArgumentOutOfRangeException(nameof(arm), arm, "Unnamed cache-state arm.")
        };
    }

    /// <summary>The cache home could not be enumerated at all.</summary>
    private static JbCacheState Unreadable()
    {
        return new JbCacheState(null, null, false, false);
    }

    /// <summary>A fresh checkout: nothing on disk, and nothing asked for it to be that way.</summary>
    private static JbCacheState Cold()
    {
        return new JbCacheState([], null, false, false);
    }

    /// <summary>The same emptiness, with a reset's tombstone explaining it.</summary>
    private static JbCacheState ColdAfterAReset()
    {
        return new JbCacheState([], null, true, false);
    }

    /// <summary>A generation a transplant has just copied in, which is why it carries no marker.</summary>
    private static JbCacheState Seeded()
    {
        return new JbCacheState([Generation], null, false, true);
    }

    /// <summary>The remnant of a killed run: a generation on disk that no run ever finished against.</summary>
    private static JbCacheState PartBuilt()
    {
        return new JbCacheState([Generation], null, false, false);
    }

    /// <summary>
    ///     A generation of this solution's own, vouched for by a marker — and, since no build is named on
    ///     either side, one the staleness judgement has nothing to say about.
    /// </summary>
    private static JbCacheState Warm()
    {
        return new JbCacheState([Generation], MarkerAge, false, false);
    }

    /// <summary>The same generation, vouched for by a marker the build about to open it wrote.</summary>
    private static JbCacheState WarmUnderThisJb()
    {
        return Warm() with { MarkerJbVersion = ThisJb, CurrentJbVersion = ThisJb };
    }

    /// <summary>The same generation again, left warm by a jb the upgrade has since replaced.</summary>
    private static JbCacheState Stale()
    {
        return Warm() with { MarkerJbVersion = AnotherJb, CurrentJbVersion = ThisJb };
    }

    /// <summary>A marker from a build of this server that recorded no jb version at all.</summary>
    private static JbCacheState StaleWithNoRecordedBuild()
    {
        return Warm() with { CurrentJbVersion = ThisJb };
    }
}