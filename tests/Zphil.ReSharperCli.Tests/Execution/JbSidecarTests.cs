using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     Pins the <see cref="JbSidecar" /> compose/parse round trip: a compose that
///     <see cref="JbSidecar.FindAll" /> could not read back would switch donor discovery off silently.
/// </summary>
public sealed class JbSidecarTests : IDisposable
{
    private const string SolutionPath = "/repo/App.sln";

    private readonly string _cacheHome;
    private readonly FakeEnvironment _environment = new();

    public JbSidecarTests()
    {
        _cacheHome = _environment.CreateTempDirectory();
    }

    public static bool OnWindows => OperatingSystem.IsWindows();

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact(Skip = "Only Windows folds a key's case.", SkipUnless = nameof(OnWindows))]
    public void ComputeKey_OnWindows_TwoSpellingsOfOnePath_ShareOneKey()
    {
        // Assert — NTFS reads both spellings as one solution, so they must share one lock rather than fork it.
        JbSidecar.ComputeKey(SolutionPath.ToUpperInvariant(), _cacheHome.ToUpperInvariant())
            .ShouldBe(JbSidecar.ComputeKey(SolutionPath, _cacheHome));
    }

    [Fact]
    public void ComputeKey_FoldsPathsThatNameTheSameCacheGeneration()
    {
        // Assert — a trailing separator must not fork one generation's lock into two...
        JbSidecar.ComputeKey(SolutionPath, _cacheHome + Path.DirectorySeparatorChar)
            .ShouldBe(JbSidecar.ComputeKey(SolutionPath, _cacheHome));

        // ...while a different solution in the same cache home is a different generation.
        JbSidecar.ComputeKey("/repo/Other.sln", _cacheHome)
            .ShouldNotBe(JbSidecar.ComputeKey(SolutionPath, _cacheHome));
    }

    [Fact]
    public void FindAll_ReadsBackWhatPathForComposed()
    {
        // Arrange — the compose/parse round trip, beside a same-keyed sidecar of another extension that a
        // filtered enumeration must not surface.
        File.WriteAllText(JbSidecar.PathFor(SolutionPath, _cacheHome, "warm"), string.Empty);
        File.WriteAllText(JbSidecar.PathFor(SolutionPath, _cacheHome, "lock"), string.Empty);

        // Act
        List<(string Key, string SidecarPath)> found = JbSidecar.FindAll(_cacheHome, "warm").ToList();

        // Assert
        (string Key, string SidecarPath) marker = found.ShouldHaveSingleItem();
        marker.Key.ShouldBe(JbSidecar.ComputeKey(SolutionPath, _cacheHome));
        marker.SidecarPath.ShouldBe(JbSidecar.PathFor(SolutionPath, _cacheHome, "warm"));
    }

    [Fact]
    public void FindAll_CacheHomeThatDoesNotExistYet_IsEmptyRatherThanThrowing()
    {
        // Assert — nothing has ever run against this cache home, which is an answer and not a fault.
        string missing = Path.Combine(_environment.CreateTempDirectory(), "never-created");

        JbSidecar.FindAll(missing, "warm").ShouldBeEmpty();
    }

    [Fact]
    public void PathFor_EverySidecarSharesOneDirectoryWithoutColliding()
    {
        // Assert — one directory, one key, an extension per sidecar: the scheme that keeps a change to any of
        // them from silently addressing another's file, so a marker, tombstone or cost-record bug cannot clobber
        // the lock it sits beside.
        string[] sidecars =
        [
            CacheHomes.RunLockPathFor(_cacheHome, SolutionPath),
            JbWarmMarker.PathFor(SolutionPath, _cacheHome),
            JbColdTombstone.PathFor(SolutionPath, _cacheHome),
            JbCostRecord.PathFor(SolutionPath, _cacheHome)
        ];

        sidecars.ShouldBeUnique();
        sidecars.Select(Path.GetDirectoryName).Distinct().ShouldHaveSingleItem();
    }
}