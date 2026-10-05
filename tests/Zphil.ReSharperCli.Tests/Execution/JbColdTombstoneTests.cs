using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     Pins the failure direction of <see cref="JbColdTombstone" />: every failure mode must read as
///     <em>reset</em>, because the only thing a "no" permits is refilling a cache the user asked to be rid of.
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
        // Assert
        JbColdTombstone.Exists(SolutionPath, _cacheHome, NullLogger.Instance).ShouldBeFalse();

        JbColdTombstone.Write(SolutionPath, _cacheHome, NullLogger.Instance);
        JbColdTombstone.Exists(SolutionPath, _cacheHome, NullLogger.Instance).ShouldBeTrue();

        JbColdTombstone.Clear(SolutionPath, _cacheHome, NullLogger.Instance);
        JbColdTombstone.Exists(SolutionPath, _cacheHome, NullLogger.Instance).ShouldBeFalse();
    }

    [Fact]
    public void Write_TwiceOverAndClearedWhenAbsent_AreBothOrdinary()
    {
        // Assert — two resets in a row and a successful run with no reset behind it are both normal, so
        // neither end of the lifecycle may object to being repeated.
        Should.NotThrow(() => JbColdTombstone.Clear(SolutionPath, _cacheHome, NullLogger.Instance));
        JbColdTombstone.Write(SolutionPath, _cacheHome, NullLogger.Instance);
        Should.NotThrow(() => JbColdTombstone.Write(SolutionPath, _cacheHome, NullLogger.Instance));
        JbColdTombstone.Exists(SolutionPath, _cacheHome, NullLogger.Instance).ShouldBeTrue();
    }

    [Fact]
    public void Write_OneSolution_SaysNothingAboutAnother()
    {
        // Arrange — the tombstone is per cache generation: resetting one checkout must not stop another being
        // seeded.
        JbColdTombstone.Write(SolutionPath, _cacheHome, NullLogger.Instance);

        // Assert
        JbColdTombstone.Exists("/repo/Other.sln", _cacheHome, NullLogger.Instance).ShouldBeFalse();
        JbColdTombstone.Exists(SolutionPath, _environment.CreateTempDirectory(), NullLogger.Instance).ShouldBeFalse();
    }

    [Fact]
    public void Exists_CacheHomeNoFileApiWillAccept_ReadsAsResetRatherThanThrowing()
    {
        // Arrange — the safe degradation here is towards reset: a question that could not be answered must not
        // be read as permission to seed.
        string invalid = _cacheHome + "\0invalid";

        // Assert — including the discharge, which runs at the end of a jb run that has already succeeded.
        Should.NotThrow(() => JbColdTombstone.Write(SolutionPath, invalid, NullLogger.Instance));
        JbColdTombstone.Exists(SolutionPath, invalid, NullLogger.Instance).ShouldBeTrue();
        Should.NotThrow(() => JbColdTombstone.Clear(SolutionPath, invalid, NullLogger.Instance));
    }

    [Fact]
    public void Write_CacheHomeThatCannotHoldIt_DoesNotThrow()
    {
        // Arrange — this runs at the end of a reset that has already deleted directories, so throwing would
        // fail a call whose work is done.
        string blocked = CacheHomes.BlockedCacheHome(_environment);

        // Act & Assert
        Should.NotThrow(() => JbColdTombstone.Write(SolutionPath, blocked, NullLogger.Instance));
    }
}