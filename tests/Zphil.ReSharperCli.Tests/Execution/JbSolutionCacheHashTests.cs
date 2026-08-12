using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     <see cref="JbSolutionCacheHash" /> reproduces a naming scheme belonging to someone else, derived by
///     observation and documented nowhere, so these tests are pins rather than derivations: the values are
///     frozen against directory names <c>jb</c> itself wrote, and any change to the recipe — the seed, the
///     multiplier, or how a character is lower-cased — fails here rather than silently addressing a cache
///     generation that does not exist.
/// </summary>
/// <remarks>
///     The failure this guards is quiet by construction. A wrong hash never names another solution's cache;
///     it names nothing, so a reset drops nothing and a transplant is skipped, and both look like an ordinary
///     empty cache home from the outside.
/// </remarks>
public sealed class JbSolutionCacheHashTests
{
    [Theory]
    [InlineData(@"C:\repo\App.sln", "-609246064")]
    [InlineData(@"C:\other\App.sln", "-1331591606")]
    [InlineData("/repo/App.sln", "-979675449")]
    public void Compute_APinnedPath_StillHashesToTheValueItWasRecordedWith(string solutionPath, string expected)
    {
        JbSolutionCacheHash.Compute(solutionPath).ShouldBe(expected);
    }

    [Fact]
    public void Compute_TheSamePathInAnotherCase_AgreesTheWayTheFilesystemDoes()
    {
        // Assert — one solution opened as "C:\Repo\App.sln" and as "c:\repo\app.sln" is one solution to
        // Windows, and jb gives it one cache generation. A hash that disagreed would send the second call
        // looking for a directory the first one's run had already filled.
        JbSolutionCacheHash.Compute(@"C:\Repo\APP.sln").ShouldBe(JbSolutionCacheHash.Compute(@"c:\repo\app.sln"));
    }

    [Fact]
    public void Compute_OneSolutionNameInTwoDirectories_Differs()
    {
        // Assert — the property the whole scheme rests on: the hash is of the *path*, so two checkouts of one
        // repository are two cache generations, and telling them apart is what this class is for.
        JbSolutionCacheHash.Compute(@"C:\repo\App.sln").ShouldNotBe(JbSolutionCacheHash.Compute(@"C:\other\App.sln"));
    }

    [Fact]
    public void Compute_NonAsciiLetters_AreNotFolded()
    {
        // Assert — the fold covers ASCII A-Z and stops there, so these two paths hash apart. Swapping in
        // char.ToLowerInvariant would read as a tidy-up and make them agree, which is why the asymmetry is
        // pinned: the recipe has to match jb's, not the one that looks more correct.
        JbSolutionCacheHash.Compute(@"C:\repö\App.sln").ShouldNotBe(JbSolutionCacheHash.Compute(@"C:\repÖ\App.sln"));
    }

    [Theory]
    // A negative hash is the ordinary case about half the time, and the minus sign is part of the name.
    [InlineData(@"C:\repo\App.sln", "_App.-609246064.00")]
    // Dots in the solution name are not separators, and survive into the directory name unchanged.
    [InlineData(@"C:\repo\Zphil.ReSharperCli.slnx", "_Zphil.ReSharperCli.-1122688508.00")]
    public void FirstGenerationDirectoryName_ComposesTheNameJbWouldCreate(string solutionPath, string expected)
    {
        JbSolutionCacheHash.FirstGenerationDirectoryName(solutionPath).ShouldBe(expected);
    }

    [Fact]
    public void FirstGenerationDirectoryName_IsReadBackByTheParserThatFindsGenerations()
    {
        // Arrange — the two halves of one undocumented scheme, written apart: this composes a name and
        // JbCacheGenerations parses one. A change to either that the other did not follow leaves a directory
        // nothing can find, so they are pinned against each other rather than only against literals.
        const string solutionPath = @"C:\repo\Zphil.ReSharperCli.slnx";
        string composed = JbSolutionCacheHash.FirstGenerationDirectoryName(solutionPath);

        // Act
        string? parsed = JbCacheGenerations.MatchHash(composed, "Zphil.ReSharperCli");

        // Assert
        parsed.ShouldBe(JbSolutionCacheHash.Compute(solutionPath));
    }
}