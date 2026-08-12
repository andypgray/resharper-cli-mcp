using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     <see cref="JbColdTombstone" /> records that a cache was dropped on purpose, so nothing later puts a
///     copy of one back. Its invariant is the mirror image of <see cref="JbWarmMarkerTests" />': every
///     failure mode must read as <em>reset</em>, because the only thing a "no" permits is refilling a cache
///     the user asked to be rid of.
/// </summary>
public sealed class JbColdTombstoneTests : IDisposable
{
    private const string SolutionPath = "/repo/App.sln";

    private readonly string _cacheHome;
    private readonly FakeEnvironment _environment = new();

    public JbColdTombstoneTests()
    {
        _cacheHome = _environment.CreateTempDirectory();
    }

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public void WriteThenClear_IsTheWholeLifecycle()
    {
        // Assert — a cache home nothing has reset carries no promise...
        JbColdTombstone.Exists(SolutionPath, _cacheHome).ShouldBeFalse();

        // ...a reset makes one...
        JbColdTombstone.Write(SolutionPath, _cacheHome);
        JbColdTombstone.Exists(SolutionPath, _cacheHome).ShouldBeTrue();

        // ...and the run that rebuilt the cache discharges it.
        JbColdTombstone.Clear(SolutionPath, _cacheHome);
        JbColdTombstone.Exists(SolutionPath, _cacheHome).ShouldBeFalse();
    }

    [Fact]
    public void Write_TwiceOverAndClearedWhenAbsent_AreBothOrdinary()
    {
        // Assert — two resets in a row and a successful run with no reset behind it are both normal, so
        // neither end of the lifecycle may object to being repeated.
        Should.NotThrow(() => JbColdTombstone.Clear(SolutionPath, _cacheHome));
        JbColdTombstone.Write(SolutionPath, _cacheHome);
        Should.NotThrow(() => JbColdTombstone.Write(SolutionPath, _cacheHome));
        JbColdTombstone.Exists(SolutionPath, _cacheHome).ShouldBeTrue();
    }

    [Fact]
    public void Write_OneSolution_SaysNothingAboutAnother()
    {
        // Arrange — the tombstone is per cache generation, like the lock and the marker beside it. Resetting
        // one checkout must not stop another being seeded.
        JbColdTombstone.Write(SolutionPath, _cacheHome);

        // Assert
        JbColdTombstone.Exists("/repo/Other.sln", _cacheHome).ShouldBeFalse();
        JbColdTombstone.Exists(SolutionPath, _environment.CreateTempDirectory()).ShouldBeFalse();
    }

    [Fact]
    public void Exists_CacheHomeNoFileApiWillAccept_ReadsAsResetRatherThanThrowing()
    {
        // Arrange — the cache home the lock and the marker both degrade on. Here the safe degradation is the
        // opposite one: a question that could not be answered must not be read as permission to seed.
        string invalid = _cacheHome + "\0invalid";

        // Assert — including the discharge, which runs at the end of a jb run that has already succeeded.
        Should.NotThrow(() => JbColdTombstone.Write(SolutionPath, invalid));
        JbColdTombstone.Exists(SolutionPath, invalid).ShouldBeTrue();
        Should.NotThrow(() => JbColdTombstone.Clear(SolutionPath, invalid));
    }

    [Fact]
    public void Write_CacheHomeThatCannotHoldIt_DoesNotThrow()
    {
        // Arrange — a *file* where the cache home should be. This runs at the end of a reset that has already
        // deleted directories, so throwing would fail a call whose work is done.
        string blocked = Path.Combine(_environment.CreateTempDirectory(), "not-a-directory");
        File.WriteAllText(blocked, string.Empty);

        // Act & Assert
        Should.NotThrow(() => JbColdTombstone.Write(SolutionPath, blocked));
    }

    [Fact]
    public void PathFor_SitsBesideTheLockFileAndTheWarmMarkerWithoutColliding()
    {
        // Assert — one directory, one key, three extensions: the scheme that keeps a change to any of them
        // from silently addressing another's file.
        string tombstone = JbColdTombstone.PathFor(SolutionPath, _cacheHome);
        string marker = JbWarmMarker.PathFor(SolutionPath, _cacheHome);
        string lockFile = JbRunLock.LockFilePathFor(_cacheHome, JbRunLock.ComputeKey(SolutionPath, _cacheHome));

        new[] { tombstone, marker, lockFile }.Distinct().Count().ShouldBe(3);
        Path.GetDirectoryName(tombstone).ShouldBe(Path.GetDirectoryName(lockFile));
        Path.GetDirectoryName(tombstone).ShouldBe(Path.GetDirectoryName(marker));
    }
}