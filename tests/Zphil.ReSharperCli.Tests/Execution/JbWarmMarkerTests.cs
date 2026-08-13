using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     <see cref="JbWarmMarker" /> answers two questions about one cache generation: "did a <c>jb</c> run
///     against it succeed recently?", which debounces the background pre-warm, and "which directory did that
///     run leave warm?", which is how one solution's cache becomes findable by another. It is a hint, never a
///     dependency, so the invariant these tests guard is one-sided — every failure mode must read as
///     <em>not</em> warm and <em>no</em> name, so the marker can permit a redundant pre-warm or a skipped
///     copy but never suppress one forever or point at the wrong directory — plus the structural fact that a
///     marker bug can never clobber the lock file it sits beside.
/// </summary>
public sealed class JbWarmMarkerTests : IDisposable
{
    private const string SolutionPath = "/repo/App.sln";

    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    private readonly string _cacheHome;
    private readonly FakeEnvironment _environment = new();

    public JbWarmMarkerTests()
    {
        _cacheHome = _environment.CreateTempDirectory();
    }

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public void IsFreshWithin_NoMarkerAtAll_ReportsStale()
    {
        // Assert — a cache home nothing has ever warmed must not read as warm.
        JbWarmMarker.IsFreshWithin(SolutionPath, _cacheHome, OneHour).ShouldBeFalse();
    }

    [Fact]
    public void IsFreshWithin_JustStamped_ReportsFresh()
    {
        // Act
        JbWarmMarker.Stamp(SolutionPath, _cacheHome);

        // Assert — the debounce reads the mtime alone, so a marker with nothing in it is still a fresh one.
        JbWarmMarker.IsFreshWithin(SolutionPath, _cacheHome, OneHour).ShouldBeTrue();
    }

    [Fact]
    public void Stamp_WithTheSolutionsGenerationOnDisk_RecordsWhichDirectoryItWarmed()
    {
        // Arrange — the marker's file name is a one-way key, so a solution that wants to know what another
        // solution left warm can only learn it from the content. This is where that content comes from.
        string generation = CacheHomes.PlantGenerationFor(_cacheHome, SolutionPath);

        // Act
        JbWarmMarker.Stamp(SolutionPath, _cacheHome);

        // Assert
        JbWarmMarker.TryReadGenerationName(JbWarmMarker.PathFor(SolutionPath, _cacheHome), _cacheHome)
            .ShouldBe(Path.GetFileName(generation));
    }

    [Fact]
    public void Stamp_SolutionWithAForkedGeneration_RecordsTheOneWrittenMostRecently()
    {
        // Arrange — a concurrent jb forks ".01" off the same hash, and both are this solution's. The one the
        // run that just succeeded actually used is the one it has only just closed, and the ordinal-latest
        // name is aged here on purpose so a name-ordered pick would fail.
        string first = CacheHomes.PlantGenerationFor(_cacheHome, SolutionPath);
        string fork = CacheHomes.PlantFork(_cacheHome, first);
        Directory.SetLastWriteTimeUtc(fork, DateTime.UtcNow - TimeSpan.FromHours(2));
        Directory.SetLastWriteTimeUtc(first, DateTime.UtcNow);

        // Act
        JbWarmMarker.Stamp(SolutionPath, _cacheHome);

        // Assert
        JbWarmMarker.TryReadGenerationName(JbWarmMarker.PathFor(SolutionPath, _cacheHome), _cacheHome)
            .ShouldBe(Path.GetFileName(first));
    }

    [Fact]
    public void Stamp_NoDirectoryMatchingTheComputedHash_LeavesTheMarkerEmptyAndTheNameUnavailable()
    {
        // Arrange — what jb changing its directory naming looks like from here: the run succeeded, and
        // nothing on disk answers to the hash this server computes.
        CacheHomes.PlantGeneration(_cacheHome, "_App.999.00");

        // Act
        JbWarmMarker.Stamp(SolutionPath, _cacheHome);

        // Assert — the debounce still works, and every feature that needs a name is told there is none rather
        // than being handed the nearest-looking directory. That is the self-disable, not a degradation.
        JbWarmMarker.IsFreshWithin(SolutionPath, _cacheHome, OneHour).ShouldBeTrue();
        new FileInfo(JbWarmMarker.PathFor(SolutionPath, _cacheHome)).Length.ShouldBe(0);
        JbWarmMarker.TryReadGenerationName(JbWarmMarker.PathFor(SolutionPath, _cacheHome), _cacheHome).ShouldBeNull();
    }

    [Fact]
    public void TryReadGenerationName_MarkerThatIsNotThere_IsNullRatherThanThrowing()
    {
        // Assert — nothing has ever warmed this generation, which is an answer and not a fault.
        JbWarmMarker.TryReadGenerationName(JbWarmMarker.PathFor(SolutionPath, _cacheHome), _cacheHome).ShouldBeNull();
    }

    [Fact]
    public void TryReadGenerationName_GenerationDeletedSinceTheMarkerWasWritten_IsNull()
    {
        // Arrange — a cache reset, or jb's own stale-cache collection, between the stamp and the read. The
        // name is still true about the past and useless about now.
        string generation = CacheHomes.PlantGenerationFor(_cacheHome, SolutionPath);
        JbWarmMarker.Stamp(SolutionPath, _cacheHome);
        Directory.Delete(generation, true);

        // Assert
        JbWarmMarker.TryReadGenerationName(JbWarmMarker.PathFor(SolutionPath, _cacheHome), _cacheHome).ShouldBeNull();
    }

    [Theory]
    // A parent reference, and a name carrying a separator: both resolve outside the cache home, which is
    // where a caller would otherwise copy from.
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../_App.999.00")]
    [InlineData("sub/_App.999.00")]
    [InlineData(@"sub\_App.999.00")]
    // A rooted path ignores the cache home entirely.
    [InlineData(@"C:\Windows")]
    public void TryReadGenerationName_ContentThatIsNotABareDirectoryName_IsRefused(string content)
    {
        // Arrange — the marker is a file in a shared cache home, so its content is untrusted input to the
        // path a transplant reads from.
        string markerPath = JbWarmMarker.PathFor(SolutionPath, _cacheHome);
        File.WriteAllText(markerPath, content);

        // Assert
        JbWarmMarker.TryReadGenerationName(markerPath, _cacheHome).ShouldBeNull();
    }

    [Fact]
    public void IsFreshWithin_MarkerOlderThanTheWindow_ReportsStale()
    {
        // Arrange — age the marker rather than the test: no sleeping, and the real window stays pinned.
        JbWarmMarker.Stamp(SolutionPath, _cacheHome);
        File.SetLastWriteTimeUtc(JbWarmMarker.PathFor(SolutionPath, _cacheHome), DateTime.UtcNow - TimeSpan.FromHours(2));

        // Assert
        JbWarmMarker.IsFreshWithin(SolutionPath, _cacheHome, OneHour).ShouldBeFalse();
    }

    [Fact]
    public void IsFreshWithin_FutureDatedMarker_ReportsStale()
    {
        // Arrange — a moved clock, or a cache home copied from another machine. Treating a negative age as
        // "fresh" would suppress pre-warming until the clock caught up, which could be forever.
        JbWarmMarker.Stamp(SolutionPath, _cacheHome);
        File.SetLastWriteTimeUtc(JbWarmMarker.PathFor(SolutionPath, _cacheHome), DateTime.UtcNow.AddDays(1));

        // Assert
        JbWarmMarker.IsFreshWithin(SolutionPath, _cacheHome, OneHour).ShouldBeFalse();
    }

    [Fact]
    public void Stamp_CacheHomeThatCannotHoldTheMarker_DoesNotThrowAndStillReportsStale()
    {
        // Arrange — a *file* where the cache home should be, so the marker can never be created. This runs
        // straight after a jb run the user asked for, so throwing here would fail a call that succeeded.
        string blocked = Path.Combine(_environment.CreateTempDirectory(), "not-a-directory");
        File.WriteAllText(blocked, string.Empty);

        // Act
        Should.NotThrow(() => JbWarmMarker.Stamp(SolutionPath, blocked));

        // Assert — an unwritable marker reads as stale, so the next pre-warm runs rather than being skipped.
        JbWarmMarker.IsFreshWithin(SolutionPath, blocked, OneHour).ShouldBeFalse();
    }

    [Fact]
    public void IsFreshWithin_PathNoFileApiWillAccept_ReportsStaleInsteadOfThrowing()
    {
        // Arrange — the same cache home the lock degrades on, so both layers agree about what is unusable.
        string invalid = _cacheHome + "\0invalid";

        // Assert — every entry point, including the one a cache reset calls once it has already deleted
        // directories, where throwing would fail a call whose work is done.
        Should.NotThrow(() => JbWarmMarker.Stamp(SolutionPath, invalid));
        JbWarmMarker.IsFreshWithin(SolutionPath, invalid, OneHour).ShouldBeFalse();
        Should.NotThrow(() => JbWarmMarker.Clear(SolutionPath, invalid));
    }

    [Fact]
    public void Stamp_OneGeneration_DoesNotWarmAnother()
    {
        // Arrange — the marker is per cache generation, exactly like the lock: warming one solution says
        // nothing about the next one in the same cache home.
        JbWarmMarker.Stamp(SolutionPath, _cacheHome);

        // Assert
        JbWarmMarker.IsFreshWithin("/repo/Other.sln", _cacheHome, OneHour).ShouldBeFalse();
        JbWarmMarker.IsFreshWithin(SolutionPath, _environment.CreateTempDirectory(), OneHour).ShouldBeFalse();
    }

    [Fact]
    public void PathFor_IsNeverTheLockFilePath()
    {
        // Assert — the structural proof that a marker bug cannot clobber the lock: they share a directory
        // and a key, and only the extension keeps them apart.
        string marker = JbWarmMarker.PathFor(SolutionPath, _cacheHome);
        string lockFile = JbRunLock.LockFilePathFor(_cacheHome, JbSidecar.ComputeKey(SolutionPath, _cacheHome));

        marker.ShouldNotBe(lockFile);
        Path.GetDirectoryName(marker).ShouldBe(Path.GetDirectoryName(lockFile));
    }
}