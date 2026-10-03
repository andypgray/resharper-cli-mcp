using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     Pins <see cref="PathSearch" /> on every platform. Each case arranges the same files everywhere and lets
///     the platform's own rule decide, so no case is gated and neither leg's skip count moves.
/// </summary>
public sealed class PathSearchTests : IDisposable
{
    private readonly FakeEnvironment _environment = new();

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public void Resolve_JbInTwoPathDirectories_ReturnsTheFirstAndSkipsEmptyEntries()
    {
        // Arrange — a spawn starts the first match, so the first match is the file worth watching. The
        // empty entry is the shape a doubled or trailing separator leaves in a real PATH.
        string first = _environment.CreateTempDirectory();
        string second = _environment.CreateTempDirectory();
        string expected = JbInstalls.PlantJbIn(first);
        JbInstalls.PlantJbIn(second);

        // Act
        string? resolved = PathSearch.Resolve("jb", PathOf(string.Empty, first, second));

        // Assert
        resolved.ShouldBe(expected);
    }

    [Fact]
    public void Resolve_AFileTheSpawnCouldNotStart_IsPassedOverForTheNextEntry()
    {
        // Arrange — a bare file called jb, which no platform starts for its own reason: Windows looks for
        // jb.exe, and elsewhere a file with no execute bit cannot be exec'd.
        string unstartable = _environment.CreateTempDirectory();
        File.WriteAllText(Path.Combine(unstartable, "jb"), "jb");
        string startable = _environment.CreateTempDirectory();
        string expected = JbInstalls.PlantJbIn(startable);

        // Act
        string? resolved = PathSearch.Resolve("jb", PathOf(unstartable, startable));

        // Assert
        resolved.ShouldBe(expected);
    }

    [Fact]
    public void Resolve_NameWithADirectory_IsThatFileWithoutSearchingPath()
    {
        // Arrange
        string jb = JbInstalls.PlantJbIn(_environment.CreateTempDirectory());

        // Act
        string? resolved = PathSearch.Resolve(jb, null);

        // Assert
        resolved.ShouldBe(jb);
    }

    [Fact]
    public void Resolve_NameWithADirectoryNamingNothingToStart_ReturnsNull()
    {
        // Arrange — a bare file called jb, which no platform starts, named by its path this time.
        string plain = Path.Combine(_environment.CreateTempDirectory(), "jb");
        File.WriteAllText(plain, "jb");

        // Act
        string? resolved = PathSearch.Resolve(plain, null);

        // Assert
        resolved.ShouldBeNull();
    }

    [Fact]
    public void Resolve_NoMatchOnPath_ReturnsNull()
    {
        // Act
        string? resolved = PathSearch.Resolve("jb", _environment.CreateTempDirectory());

        // Assert
        resolved.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Resolve_NoPathToSearch_ReturnsNull(string? pathVariable)
    {
        // Act
        string? resolved = PathSearch.Resolve("jb", pathVariable);

        // Assert
        resolved.ShouldBeNull();
    }

    private static string PathOf(params string[] directories)
    {
        return string.Join(Path.PathSeparator, directories);
    }
}