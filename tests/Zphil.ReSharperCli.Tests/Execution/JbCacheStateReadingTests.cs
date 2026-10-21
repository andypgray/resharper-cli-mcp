using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     Pins the two <see cref="JbCacheState" /> members that read a cache home: <see cref="JbCacheState.Read" />,
///     which assembles a state from what is on disk, and <see cref="JbCacheState.TryMeasure" />, which walks the
///     generations it names.
/// </summary>
public sealed class JbCacheStateReadingTests : IDisposable
{
    private const string SolutionPath = "/repo/App.sln";

    private const string ThisJb = "2026.2.1";

    private static readonly TimeSpan SevenMinutes = TimeSpan.FromMinutes(7);

    private readonly string _cacheHome;
    private readonly FakeEnvironment _environment = new();

    public JbCacheStateReadingTests()
    {
        _cacheHome = _environment.CreateTempDirectory();
    }

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public void Read_NothingOnDisk_IsCold()
    {
        // Act
        JbCacheState state = Read();

        // Assert
        state.Kind.ShouldBe(JbCacheKind.Cold);
        state.Generations.ShouldBeEmpty();
        state.ResetRecorded.ShouldBeFalse();
    }

    [Fact]
    public void Read_AfterAReset_IsColdWithTheResetRecorded()
    {
        // Arrange
        JbColdTombstone.Write(SolutionPath, _cacheHome, NullLogger.Instance);

        // Act
        JbCacheState state = Read();

        // Assert
        state.Kind.ShouldBe(JbCacheKind.Cold);
        state.ResetRecorded.ShouldBeTrue();
    }

    [Fact]
    public void Read_AGenerationThisBuildLeftWarm_IsWarmAndNamesIt()
    {
        // Arrange
        string generation = CacheHomes.PlantWarmDonor(_cacheHome, SolutionPath, ThisJb);

        // Act
        JbCacheState state = Read();

        // Assert
        state.Kind.ShouldBe(JbCacheKind.Warm);
        state.Generations.ShouldBe([Path.GetFileName(generation)]);
        state.WarmMarkerAge.ShouldNotBeNull();
        state.MarkerJbVersion.ShouldBe(ThisJb);
    }

    [Fact]
    public void Read_ColdWithAColdRunRecorded_CarriesWhatThatRunTook()
    {
        // Arrange
        JbCostRecord.Stamp(SolutionPath, _cacheHome, JbCostBand.Cold, SevenMinutes, NullLogger.Instance);

        // Act
        JbCacheState state = Read();

        // Assert
        state.LastComparableCost.ShouldBe(SevenMinutes);
    }

    [Fact]
    public void Read_WarmWithAColdRunRecorded_CarriesNoFigure()
    {
        // Arrange — a warm state falls in no band, so the cold figure beside it predicts nothing about it.
        CacheHomes.PlantWarmDonor(_cacheHome, SolutionPath, ThisJb);
        JbCostRecord.Stamp(SolutionPath, _cacheHome, JbCostBand.Cold, SevenMinutes, NullLogger.Instance);

        // Act
        JbCacheState state = Read();

        // Assert
        state.Kind.ShouldBe(JbCacheKind.Warm);
        state.LastComparableCost.ShouldBeNull();
    }

    [Fact]
    public void TryMeasure_AnUnreadableCacheHome_MeasuresNothing()
    {
        new JbCacheState(null, null, false, false).TryMeasure(_cacheHome).ShouldBeNull();
    }

    [Fact]
    public void TryMeasure_NoGenerationOnDisk_MeasuresNothing()
    {
        new JbCacheState([], null, false, false).TryMeasure(_cacheHome).ShouldBeNull();
    }

    [Fact]
    public void TryMeasure_TwoGenerations_TotalsEveryFileInBothTrees()
    {
        // Arrange — files nested below the generation's own directory, as jb leaves them, so a walk that read
        // only the top level would come up short.
        PlantFile("_App.1.00", Path.Combine("Db", "CURRENT"), 5);
        PlantFile("_App.1.01", Path.Combine("Db", "CURRENT"), 7);
        PlantFile("_App.1.01", Path.Combine("Db", "Deep", "blob"), 100);
        JbCacheState state = new(["_App.1.00", "_App.1.01"], null, false, false);

        // Act
        (long Bytes, int Files)? measured = state.TryMeasure(_cacheHome);

        // Assert
        measured.ShouldBe((112L, 3));
    }

    [Fact]
    public void TryMeasure_AGenerationNoLongerOnDisk_MeasuresNothingRatherThanThrowing()
    {
        // Arrange — the measurement only feeds a Debug line, so a walk that fails must not fail the run.
        JbCacheState state = new(["_App.1.00"], null, false, false);

        // Act and assert
        state.TryMeasure(_cacheHome).ShouldBeNull();
    }

    private JbCacheState Read()
    {
        return JbCacheState.Read(SolutionPath, _cacheHome, false, ThisJb, NullLogger.Instance);
    }

    private void PlantFile(string generation, string relativePath, int length)
    {
        string path = Path.Combine(_cacheHome, generation, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
    }
}