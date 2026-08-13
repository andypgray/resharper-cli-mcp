using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     <see cref="JbSidecar" /> is the one spelling of how this server names its own files in a cache home,
///     composing and parsing in the same class. The round trip is the invariant worth pinning: the lock, the
///     warm marker, the cold tombstone, and donor discovery all address sidecars through this scheme, and a
///     compose that <see cref="JbSidecar.FindAll" /> could not read back would switch donor discovery off
///     silently.
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

    public void Dispose()
    {
        _environment.Dispose();
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
        var found = JbSidecar.FindAll(_cacheHome, "warm").ToList();

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
}