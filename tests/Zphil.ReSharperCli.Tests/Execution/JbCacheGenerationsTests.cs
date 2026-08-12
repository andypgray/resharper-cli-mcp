using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     <see cref="JbCacheGenerations" /> reads an undocumented directory layout on behalf of a caller that
///     deletes what it returns, so the invariant under test is one-sided in the opposite direction to
///     <see cref="JbWarmMarkerTests" />: anything not provably this solution's cache must be skipped. The
///     sibling-solution cases model a shape that occurs in real cache homes — <c>_App.100200300.00</c>
///     sitting beside <c>_App.Core.400500600.00</c>, the pair a loose prefix match would conflate.
/// </summary>
public sealed class JbCacheGenerationsTests : IDisposable
{
    private readonly FakeEnvironment _environment = new();

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Theory]
    // The ordinary shape, and the negative hash jb produces about half the time.
    [InlineData("_App.123456.00", "App", "123456")]
    [InlineData("_App.-1749040816.00", "App", "-1749040816")]
    // A forked generation is the same solution: same hash, higher generation number.
    [InlineData("_App.123456.01", "App", "123456")]
    // Dots in the solution name are ordinary, and are not a separator to guess at.
    [InlineData("_Zphil.ReSharperCli.-1625048228.00", "Zphil.ReSharperCli", "-1625048228")]
    [InlineData("_App.Core.400500600.00", "App.Core", "400500600")]
    public void MatchHash_ThisSolutionsGeneration_ReturnsTheHash(string directoryName, string solutionName, string expected)
    {
        JbCacheGenerations.MatchHash(directoryName, solutionName).ShouldBe(expected);
    }

    [Theory]
    // The one that matters: a longer-named solution's cache starts with the shorter one's prefix, and only
    // the strict {hash}.{generation} parse keeps them apart.
    [InlineData("_App.Core.400500600.00", "App")]
    [InlineData("_Zphil.ReSharperCli.-1625048228.00", "Zphil")]
    // A different solution entirely.
    [InlineData("_Other.123456.00", "App")]
    // Malformed tails: no generation, a non-numeric hash, a non-numeric generation, a trailing suffix.
    [InlineData("_App.123456", "App")]
    [InlineData("_App.abc.00", "App")]
    [InlineData("_App.123456.xx", "App")]
    [InlineData("_App.123456.00.deleting", "App")]
    // jb's leading underscore is part of the scheme, so a directory without it is not one of these.
    [InlineData("App.123456.00", "App")]
    public void MatchHash_AnythingElse_ReturnsNull(string directoryName, string solutionName)
    {
        JbCacheGenerations.MatchHash(directoryName, solutionName).ShouldBeNull();
    }

    [Fact]
    public void Find_CacheHomeWithSeveralSolutions_ReturnsOnlyThisSolutionsGenerations()
    {
        // Arrange — one solution's two generations, a same-prefixed sibling solution, an unrelated solution,
        // and the server's own sidecar files, which are files rather than directories.
        string cacheHome = _environment.CreateTempDirectory();
        CacheHomes.PlantGeneration(cacheHome, "_App.100200300.00");
        CacheHomes.PlantGeneration(cacheHome, "_App.100200300.01");
        CacheHomes.PlantGeneration(cacheHome, "_App.Core.400500600.00");
        CacheHomes.PlantGeneration(cacheHome, "_Other.99.00");
        File.WriteAllText(Path.Combine(cacheHome, ".resharper-cli-mcp-abc.lock"), string.Empty);

        // Act
        var generations = JbCacheGenerations.Find(cacheHome, "App");

        // Assert — both generations of App, one hash between them, and nothing belonging to a neighbour.
        generations.Select(generation => generation.Name)
            .ShouldBe(["_App.100200300.00", "_App.100200300.01"]);
        generations.Select(generation => generation.Hash).Distinct().ShouldHaveSingleItem();
        generations.ShouldAllBe(generation => Directory.Exists(generation.FullPath));
    }

    [Fact]
    public void Find_TwoSolutionsSharingAFileName_ReturnsBothHashes()
    {
        // Arrange — the ambiguity the reset tool refuses on, reproduced here because this is where it becomes
        // visible: same file name, different directories, so jb hashed them apart and recorded neither path.
        string cacheHome = _environment.CreateTempDirectory();
        CacheHomes.PlantGeneration(cacheHome, "_App.1344362500.00");
        CacheHomes.PlantGeneration(cacheHome, "_App.-1749040816.00");

        // Act
        var generations = JbCacheGenerations.Find(cacheHome, "App");

        // Assert
        generations.Select(generation => generation.Hash).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public void Find_CacheHomeThatDoesNotExistYet_IsEmptyRatherThanThrowing()
    {
        // Assert — nothing has ever run against this cache home, which is "nothing to drop" and not a fault.
        string missing = Path.Combine(_environment.CreateTempDirectory(), "never-created");

        JbCacheGenerations.Find(missing, "App").ShouldBeEmpty();
    }
}