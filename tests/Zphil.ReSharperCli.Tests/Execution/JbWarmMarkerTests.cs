using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     <see cref="JbWarmMarker" /> is the debounce behind the background pre-warm: it answers "did a
///     <c>jb</c> run against this cache generation succeed recently?" so a session start does not re-analyse
///     an already-warm solution. It is a hint, never a dependency, so the invariant these tests guard is
///     one-sided — every failure mode must read as <em>not</em> warm, so the marker can permit a redundant
///     pre-warm but never suppress one forever — plus the structural fact that a marker bug can never
///     clobber the lock file it sits beside.
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

        // Assert — and the marker carries its meaning entirely in its mtime.
        JbWarmMarker.IsFreshWithin(SolutionPath, _cacheHome, OneHour).ShouldBeTrue();
        new FileInfo(JbWarmMarker.PathFor(SolutionPath, _cacheHome)).Length.ShouldBe(0);
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

        // Assert
        Should.NotThrow(() => JbWarmMarker.Stamp(SolutionPath, invalid));
        JbWarmMarker.IsFreshWithin(SolutionPath, invalid, OneHour).ShouldBeFalse();
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
        string lockFile = JbRunLock.LockFilePathFor(_cacheHome, JbRunLock.ComputeKey(SolutionPath, _cacheHome));

        marker.ShouldNotBe(lockFile);
        Path.GetDirectoryName(marker).ShouldBe(Path.GetDirectoryName(lockFile));
    }
}