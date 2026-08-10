using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Tests.Formatting;

/// <summary>
///     Pins <see cref="CacheResetFormatter" />'s three shapes. The load-bearing one is the closing line: it
///     promises the next call is cold, and must appear only when something was actually deleted.
/// </summary>
public sealed class CacheResetFormatterTests
{
    private const string SolutionPath = "/repo/App.sln";
    private const string CacheHome = "/home/u/.jb-cache";

    [Fact]
    public void Format_GenerationsDropped_ListsThemAndWarnsTheNextCallIsCold()
    {
        // Arrange
        CacheResetOutcome outcome = new(SolutionPath, CacheHome, ["_App.123.00", "_App.123.01"], []);

        // Act
        string result = CacheResetFormatter.Format(outcome);

        // Assert
        result.ShouldBe(
            $"Dropped 2 ReSharper cache generation(s) for \"{SolutionPath}\" under \"{CacheHome}\":\n"
            + "  - _App.123.00\n"
            + "  - _App.123.01\n"
            + "The next inspect or cleanup against this solution rebuilds the cache from cold, which can take minutes.");
    }

    [Fact]
    public void Format_NothingCached_SaysSoWithoutPromisingAnythingChanged()
    {
        // Arrange
        CacheResetOutcome outcome = new(SolutionPath, CacheHome, [], []);

        // Act
        string result = CacheResetFormatter.Format(outcome);

        // Assert
        result.ShouldBe(
            $"No ReSharper cache generation for \"{SolutionPath}\" was found under \"{CacheHome}\". "
            + "Nothing to drop, so the next inspect or cleanup builds the cache from cold anyway.");
    }

    [Fact]
    public void Format_EverythingFailedToDelete_DoesNotClaimTheNextCallIsCold()
    {
        // Arrange — the cache is still there and still warm. Telling the caller to expect a slow rebuild would
        // send them to wait out a cold run that is not going to happen.
        CacheResetOutcome outcome = new(
            SolutionPath, CacheHome, [], [new CacheResetFailure("_App.123.00", "The process cannot access the file.")]);

        // Act
        string result = CacheResetFormatter.Format(outcome);

        // Assert
        result.ShouldNotContain("rebuilds the cache from cold");
        result.ShouldBe(
            "Could not drop 1 generation(s):\n"
            + "  - _App.123.00: The process cannot access the file.\n"
            + "A generation that will not delete is usually one another jb still has open. Retry once it has "
            + "finished; this tool is safe to run again.");
    }

    [Fact]
    public void Format_PartialSuccess_ReportsBothHalves()
    {
        // Arrange — one generation went, the fork did not.
        CacheResetOutcome outcome = new(
            SolutionPath, CacheHome, ["_App.123.00"], [new CacheResetFailure("_App.123.01", "Access to the path is denied.")]);

        // Act
        string result = CacheResetFormatter.Format(outcome);

        // Assert
        result.ShouldContain("Dropped 1 ReSharper cache generation(s)");
        result.ShouldContain("  - _App.123.01: Access to the path is denied.");
        result.ShouldEndWith(
            "The next inspect or cleanup against this solution rebuilds the cache from cold, which can take minutes.");
    }
}