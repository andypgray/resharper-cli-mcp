using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     <see cref="JbCostRecord" /> remembers what a <c>jb</c> run cost, keyed by the cache state it started
///     from. Two invariants carry the class. The key has to hold, because the measured spread between bands
///     on one solution is 497 seconds cold against 39 warm and a figure quoted under the wrong band is worse
///     than no figure at all. And every failure has to read as <em>no figure</em>, which is
///     <see cref="JbWarmMarker" />'s direction rather than <see cref="JbColdTombstone" />'s: what is lost is a
///     hint, and it is lost at the tail of a <c>jb</c> run the user already paid minutes for.
/// </summary>
public sealed class JbCostRecordTests : IDisposable
{
    private const string SolutionPath = "/repo/App.sln";

    private readonly string _cacheHome;
    private readonly FakeEnvironment _environment = new();

    public JbCostRecordTests()
    {
        _cacheHome = _environment.CreateTempDirectory();
    }

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public void StampThenTryRead_RoundTripsAtWholeSeconds()
    {
        // Act — the resolution every reader renders at, so what goes in comes back out unrounded.
        JbCostRecord.Stamp(SolutionPath, _cacheHome, JbCostBand.Cold, TimeSpan.FromSeconds(497), NullLogger.Instance);

        // Assert
        Read(JbCostBand.Cold).ShouldBe(TimeSpan.FromSeconds(497));
    }

    [Fact]
    public void Stamp_ASecondBand_LeavesTheFirstStanding()
    {
        // Arrange — the whole reason there is a file rather than a single number. A solution seeded once and
        // then run warm has two figures worth keeping, and they are minutes apart.
        JbCostRecord.Stamp(SolutionPath, _cacheHome, JbCostBand.Seeded, TimeSpan.FromSeconds(456), NullLogger.Instance);

        // Act
        JbCostRecord.Stamp(SolutionPath, _cacheHome, JbCostBand.Warm, TimeSpan.FromSeconds(39), NullLogger.Instance);

        // Assert
        Read(JbCostBand.Seeded).ShouldBe(TimeSpan.FromSeconds(456));
        Read(JbCostBand.Warm).ShouldBe(TimeSpan.FromSeconds(39));
    }

    [Fact]
    public void Stamp_TheSameBandTwice_KeepsTheLatestRatherThanAccumulating()
    {
        // Arrange — a solution grows and its warm run slows. The last comparable run is the one worth
        // quoting, and a file that appended would grow without bound while quoting the oldest figure in it.
        JbCostRecord.Stamp(SolutionPath, _cacheHome, JbCostBand.Warm, TimeSpan.FromSeconds(39), NullLogger.Instance);

        // Act
        JbCostRecord.Stamp(SolutionPath, _cacheHome, JbCostBand.Warm, TimeSpan.FromSeconds(81), NullLogger.Instance);

        // Assert
        Read(JbCostBand.Warm).ShouldBe(TimeSpan.FromSeconds(81));
        File.ReadAllLines(JbCostRecord.PathFor(SolutionPath, _cacheHome)).ShouldHaveSingleItem();
    }

    [Fact]
    public void TryRead_NothingEverStamped_IsNullRatherThanThrowing()
    {
        // Assert — the first run of a solution, which is the case that most wants a figure and is the one
        // case that can never have one.
        Read(JbCostBand.Cold).ShouldBeNull();
    }

    [Fact]
    public void TryRead_ABandNothingHasStamped_SaysNothingAboutTheOnesThatHave()
    {
        // Arrange — a freshly seeded checkout has a seeded figure and no warm one, and must not answer the
        // warm question with the seeded number: 456 seconds against a run that will take 39.
        JbCostRecord.Stamp(SolutionPath, _cacheHome, JbCostBand.Seeded, TimeSpan.FromSeconds(456), NullLogger.Instance);

        // Assert
        Read(JbCostBand.Warm).ShouldBeNull();
        Read(JbCostBand.Cold).ShouldBeNull();
    }

    [Fact]
    public void Stamp_ALineThisBuildDoesNotRecognise_IsIgnoredOnReadAndKeptOnWrite()
    {
        // Arrange — a band a later build records, met by this one. Reading it as a figure would be a guess;
        // dropping it would make two builds sharing a cache home erase each other's measurements every run.
        File.WriteAllText(JbCostRecord.PathFor(SolutionPath, _cacheHome), "lukewarm 120\ncold 497\n");

        // Act
        JbCostRecord.Stamp(SolutionPath, _cacheHome, JbCostBand.Warm, TimeSpan.FromSeconds(39), NullLogger.Instance);

        // Assert — the unknown line survives verbatim, the known ones read back, and neither band this build
        // understands has picked up the stranger's number.
        File.ReadAllLines(JbCostRecord.PathFor(SolutionPath, _cacheHome)).ShouldContain("lukewarm 120");
        Read(JbCostBand.Cold).ShouldBe(TimeSpan.FromSeconds(497));
        Read(JbCostBand.Warm).ShouldBe(TimeSpan.FromSeconds(39));
    }

    [Theory]
    // Not a number at all, a signed one, an empty value, and a fractional one: every shape a hand-edited or
    // half-written file takes. None may reach a sentence claiming to be a measurement.
    [InlineData("cold soon\n")]
    [InlineData("cold -497\n")]
    [InlineData("cold \n")]
    [InlineData("cold 497.5\n")]
    [InlineData("\0binary junk\n")]
    public void TryRead_ContentThatIsNotAWholeNumberOfSeconds_IsNullRatherThanThrowing(string content)
    {
        // Arrange — the record is a file in a shared cache home, so its content is untrusted input to a
        // sentence a user reads as fact.
        File.WriteAllText(JbCostRecord.PathFor(SolutionPath, _cacheHome), content);

        // Assert
        Should.NotThrow(() => Read(JbCostBand.Cold)).ShouldBeNull();
    }

    [Fact]
    public void Stamp_MalformedContent_StillRecordsTheRunThatJustFinished()
    {
        // Arrange — a file that cannot be read must not become a file that cannot be written either, or one
        // bad line would retire the feature for that solution permanently.
        File.WriteAllText(JbCostRecord.PathFor(SolutionPath, _cacheHome), "cold soon\n");

        // Act
        JbCostRecord.Stamp(SolutionPath, _cacheHome, JbCostBand.Cold, TimeSpan.FromSeconds(497), NullLogger.Instance);

        // Assert
        Read(JbCostBand.Cold).ShouldBe(TimeSpan.FromSeconds(497));
    }

    [Fact]
    public void Stamp_CacheHomeThatCannotHoldTheRecord_DegradesQuietlyRatherThanThrowing()
    {
        // Arrange — this runs at the tail of a jb run the user already waited minutes for, so throwing would
        // fail a call whose work is done. The direction is the warm marker's, not the tombstone's: what is
        // lost here is a hint, never a promise, so it goes no louder than debug.
        CapturingLoggerProvider logs = new();
        ILogger logger = Logs.For<JbCostRecordTests>(logs);
        string blocked = CacheHomes.BlockedCacheHome(_environment);

        // Act & Assert — every entry point, including the clear a cache reset makes after deleting
        // directories.
        Should.NotThrow(() => JbCostRecord.Stamp(SolutionPath, blocked, JbCostBand.Cold, TimeSpan.FromSeconds(497), logger));
        Should.NotThrow(() => JbCostRecord.TryRead(SolutionPath, blocked, JbCostBand.Cold, logger)).ShouldBeNull();
        Should.NotThrow(() => JbCostRecord.Clear(SolutionPath, blocked, logger));

        logs.Entries.ShouldNotBeEmpty();
        logs.Entries.ShouldAllBe(entry => entry.Level == LogLevel.Debug);
    }

    [Fact]
    public void TryRead_PathNoFileApiWillAccept_ReportsNoFigureInsteadOfThrowing()
    {
        // Arrange — the cache home every other sidecar here degrades on, where even the key cannot be
        // derived.
        string invalid = _cacheHome + "\0invalid";

        // Assert
        Should.NotThrow(() => JbCostRecord.Stamp(SolutionPath, invalid, JbCostBand.Warm, TimeSpan.FromSeconds(39), NullLogger.Instance));
        JbCostRecord.TryRead(SolutionPath, invalid, JbCostBand.Warm, NullLogger.Instance).ShouldBeNull();
        Should.NotThrow(() => JbCostRecord.Clear(SolutionPath, invalid, NullLogger.Instance));
    }

    [Fact]
    public void Stamp_OneSolution_SaysNothingAboutAnother()
    {
        // Arrange — the record is per cache generation, exactly like the lock and the marker beside it. Two
        // checkouts of one repository share a cache home and are hashed apart, and their costs differ by
        // whether either has ever been analysed.
        JbCostRecord.Stamp(SolutionPath, _cacheHome, JbCostBand.Warm, TimeSpan.FromSeconds(39), NullLogger.Instance);

        // Assert
        JbCostRecord.TryRead("/repo/Other.sln", _cacheHome, JbCostBand.Warm, NullLogger.Instance).ShouldBeNull();
        JbCostRecord.TryRead(SolutionPath, _environment.CreateTempDirectory(), JbCostBand.Warm, NullLogger.Instance).ShouldBeNull();
    }

    [Fact]
    public void Clear_AfterAReset_ForgetsEveryBandRatherThanOne()
    {
        // Arrange — a reset ends the lineage every figure describes, whichever band recorded it.
        JbCostRecord.Stamp(SolutionPath, _cacheHome, JbCostBand.Cold, TimeSpan.FromSeconds(497), NullLogger.Instance);
        JbCostRecord.Stamp(SolutionPath, _cacheHome, JbCostBand.Warm, TimeSpan.FromSeconds(39), NullLogger.Instance);

        // Act — and twice over, because a reset of an already-reset solution is an ordinary thing to do.
        JbCostRecord.Clear(SolutionPath, _cacheHome, NullLogger.Instance);
        Should.NotThrow(() => JbCostRecord.Clear(SolutionPath, _cacheHome, NullLogger.Instance));

        // Assert
        Read(JbCostBand.Cold).ShouldBeNull();
        Read(JbCostBand.Warm).ShouldBeNull();
    }

    [Fact]
    public void Label_IsTheSameSpellingTheFileAndTheProseUse()
    {
        // Assert — one spelling, because the file's tokens and the sentence a user reads are both produced
        // from it, and a second spelling would let the two drift while both kept passing.
        JbCostRecord.Label(JbCostBand.Cold).ShouldBe("cold");
        JbCostRecord.Label(JbCostBand.Seeded).ShouldBe("seeded");
        JbCostRecord.Label(JbCostBand.Warm).ShouldBe("warm");
    }

    [Fact]
    public void Label_EveryBand_HasASpellingOfItsOwn()
    {
        // Assert — a band added to the enum and not to the switch would throw at the tail of a run that had
        // just succeeded, and two bands sharing a spelling would key onto one line in the file and overwrite
        // each other silently. The file is keyed by these tokens, so both are correctness rather than tidiness.
        JbCostBand[] bands = Enum.GetValues<JbCostBand>();

        bands.Select(JbCostRecord.Label).Distinct().Count().ShouldBe(bands.Length);
    }

    [Fact]
    public void PathFor_SitsBesideTheOtherThreeSidecarsWithoutColliding()
    {
        // Assert — one directory, one key, four extensions: the scheme that keeps a change to any of them
        // from silently addressing another's file.
        string cost = JbCostRecord.PathFor(SolutionPath, _cacheHome);
        string tombstone = JbColdTombstone.PathFor(SolutionPath, _cacheHome);
        string marker = JbWarmMarker.PathFor(SolutionPath, _cacheHome);
        string lockFile = JbRunLock.LockFilePathFor(_cacheHome, JbSidecar.ComputeKey(SolutionPath, _cacheHome));

        new[] { cost, tombstone, marker, lockFile }.Distinct().Count().ShouldBe(4);
        Path.GetDirectoryName(cost).ShouldBe(Path.GetDirectoryName(lockFile));
        Path.GetDirectoryName(cost).ShouldBe(Path.GetDirectoryName(marker));
        Path.GetDirectoryName(cost).ShouldBe(Path.GetDirectoryName(tombstone));
    }

    private TimeSpan? Read(JbCostBand band)
    {
        return JbCostRecord.TryRead(SolutionPath, _cacheHome, band, NullLogger.Instance);
    }
}