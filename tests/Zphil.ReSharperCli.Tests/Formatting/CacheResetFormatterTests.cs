using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Tests.Formatting;

/// <summary>
///     Pins <see cref="CacheResetFormatter" />'s shapes. Two lines are load-bearing: the closing one promises
///     the next call is cold and must appear only when something was actually deleted, and the left-alone one
///     has to say why a directory the caller can see is still there, or the report reads as a partial failure.
///     The closing line has a second form, for a reclaim: with no checkout at that path there is no next call
///     to be cold, so promising one would describe a run that cannot happen.
/// </summary>
public sealed class CacheResetFormatterTests
{
    private const string SolutionPath = "/repo/App.sln";
    private const string CacheHome = "/home/u/.jb-cache";

    private const string NothingFound =
        $"No ReSharper cache generation for \"{SolutionPath}\" was found under \"{CacheHome}\". "
        + "Nothing to drop, so the next inspect or cleanup builds the cache from cold anyway.";

    private const string NothingFoundForARemovedCheckout =
        $"No ReSharper cache generation for \"{SolutionPath}\" was found under \"{CacheHome}\". Nothing to drop.";

    private const string NothingRebuildsThis =
        "The solution file does not exist, so nothing rebuilds this cache. A checkout created at that path "
        + "later starts like any other new one, seeded from a sibling checkout where one is warm.";

    private const string LeftOneAlone =
        "Left 1 generation(s) alone, whose names hash to a different solution path — another checkout or copy "
        + "of a solution with this file name:";

    [Fact]
    public void Format_GenerationsDropped_ListsThemAndWarnsTheNextCallIsCold()
    {
        // Arrange
        CacheResetOutcome outcome = new(SolutionPath, CacheHome, ["_App.123.00", "_App.123.01"], [], []);

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
    public void Format_GenerationsDroppedForARemovedCheckout_SaysNothingRebuildsThem()
    {
        // Arrange — the reclaim. The cold-cost warning is the one line that would be false here: there is no
        // checkout at that path to make the next call, and a caller told to expect a slow rebuild would be
        // waiting for a run that cannot happen.
        CacheResetOutcome outcome = new(SolutionPath, CacheHome, ["_App.123.00"], [], [], false);

        // Act
        string result = CacheResetFormatter.Format(outcome);

        // Assert
        result.ShouldBe(
            $"Dropped 1 ReSharper cache generation(s) for \"{SolutionPath}\" under \"{CacheHome}\":\n"
            + "  - _App.123.00\n"
            + NothingRebuildsThis);
    }

    [Fact]
    public void Format_NothingCachedForARemovedCheckout_StopsAtNothingToDrop()
    {
        // Arrange — a path guessed at, or one already reclaimed. The ordinary wording closes by saying the
        // next call builds the cache from cold anyway, which is a claim about a call nobody can make.
        CacheResetOutcome outcome = new(SolutionPath, CacheHome, [], [], [], false);

        // Act
        string result = CacheResetFormatter.Format(outcome);

        // Assert
        result.ShouldBe(NothingFoundForARemovedCheckout);
    }

    [Fact]
    public void Format_NothingCached_SaysSoWithoutPromisingAnythingChanged()
    {
        // Arrange
        CacheResetOutcome outcome = new(SolutionPath, CacheHome, [], [], []);

        // Act
        string result = CacheResetFormatter.Format(outcome);

        // Assert
        result.ShouldBe(NothingFound);
    }

    [Fact]
    public void Format_EverythingFailedToDelete_DoesNotClaimTheNextCallIsCold()
    {
        // Arrange — the cache is still there and still warm. Telling the caller to expect a slow rebuild would
        // send them to wait out a cold run that is not going to happen.
        CacheResetOutcome outcome = new(
            SolutionPath, CacheHome, [], [], [new CacheResetFailure("_App.123.00", "The process cannot access the file.")]);

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
    public void Format_AReasonSpanningLines_FlattensItOntoItsOneListItem()
    {
        // Arrange — the reason is whatever the filesystem said, carried raw in the outcome; some of its
        // messages span lines, and a failure's list item must not spill into the report as body text.
        CacheResetOutcome outcome = new(
            SolutionPath, CacheHome, [], [], [new CacheResetFailure("_App.123.00", "The process\r\ncannot access the file.")]);

        // Act
        string result = CacheResetFormatter.Format(outcome);

        // Assert
        result.ShouldContain("  - _App.123.00: The process cannot access the file.");
    }

    [Fact]
    public void Format_PartialSuccess_ReportsBothHalves()
    {
        // Arrange — one generation went, the fork did not.
        CacheResetOutcome outcome = new(
            SolutionPath, CacheHome, ["_App.123.00"], [], [new CacheResetFailure("_App.123.01", "Access to the path is denied.")]);

        // Act
        string result = CacheResetFormatter.Format(outcome);

        // Assert
        result.ShouldContain("Dropped 1 ReSharper cache generation(s)");
        result.ShouldContain("  - _App.123.01: Access to the path is denied.");
        result.ShouldEndWith(
            "The next inspect or cleanup against this solution rebuilds the cache from cold, which can take minutes.");
    }

    [Fact]
    public void Format_AGenerationLeftAlone_NamesItAndWhyItIsStillThere()
    {
        // Arrange — a second checkout's cache, sharing the solution file name. A caller looking at the cache
        // home afterwards sees a directory that was not dropped, so the report has to account for it.
        CacheResetOutcome outcome = new(SolutionPath, CacheHome, ["_App.123.00"], ["_App.999.00"], []);

        // Act
        string result = CacheResetFormatter.Format(outcome);

        // Assert
        result.ShouldBe(
            $"Dropped 1 ReSharper cache generation(s) for \"{SolutionPath}\" under \"{CacheHome}\":\n"
            + "  - _App.123.00\n"
            + LeftOneAlone + "\n"
            + "  - _App.999.00\n"
            + "The next inspect or cleanup against this solution rebuilds the cache from cold, which can take minutes.");
    }

    [Fact]
    public void Format_OnlyAnotherCheckoutsGeneration_SaysNothingOfOursWasFoundRatherThanNothingAtAll()
    {
        // Arrange — nothing was deleted and the cache home is plainly not empty. Reporting only the first half
        // would read as a tool that could not see what the caller can.
        CacheResetOutcome outcome = new(SolutionPath, CacheHome, [], ["_App.999.00"], []);

        // Act
        string result = CacheResetFormatter.Format(outcome);

        // Assert
        result.ShouldBe(
            NothingFound + "\n"
                         + LeftOneAlone + "\n"
                         + "  - _App.999.00");
    }
}