using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Formatting;

namespace Zphil.ReSharperCli.Tests.Formatting;

/// <summary>Pins how <see cref="ProgressiveRenderer" /> walks the <see cref="DetailLevel" /> ladder.</summary>
public sealed class ProgressiveRendererTests
{
    [Theory]
    [InlineData(50)]
    [InlineData(100)] // the boundary is inclusive: exactly at the budget still fits
    public void Render_FullWithinTheBudget_ReturnsItVerbatim(int length)
    {
        // Arrange
        var output = new string('x', length);

        // Act
        ProgressiveRendering rendering = ProgressiveRenderer.Render("input", (_, _) => output, 100);

        // Assert
        rendering.Level.ShouldBe(DetailLevel.Full);
        rendering.Text.ShouldBe(output);
    }

    [Fact]
    public void Render_FitsAtHigh_AppendsTheReductionNote()
    {
        // Act — Full is too large; High plus its reduction note fits.
        ProgressiveRendering rendering = ProgressiveRenderer.Render("input", FullOverflows, 400);

        // Assert — the whole note, layout and default description included: the budget it names and the
        // level it settled at are what a caller reads to decide whether to narrow the scan.
        rendering.Level.ShouldBe(DetailLevel.High);
        rendering.Text.ShouldBe(
            new string('y', 50)
            + "\n\n--- DETAIL REDUCED ---\n"
            + "Output exceeded the 400 character limit. Reduced to High: "
            + "lower-signal detail was collapsed to fit the output budget.");
    }

    [Fact]
    public void Render_FitsAtLow_NamesLowLevel()
    {
        // Arrange — Full, High and Medium each render something different and each too large, so the walk
        // genuinely tries all three before Low plus its note fits. (Identical oversized levels would be
        // content-skipped instead, which is the byte-identical test below.)
        ProgressiveRendering rendering = ProgressiveRenderer.Render(
            "input",
            (_, level) => level switch
            {
                DetailLevel.Full => new string('f', 1000),
                DetailLevel.High => new string('h', 900),
                DetailLevel.Medium => new string('m', 800),
                _ => new string('y', 50)
            },
            400);

        // Assert
        rendering.Level.ShouldBe(DetailLevel.Low);
        rendering.Text.ShouldContain("Reduced to Low");
    }

    [Fact]
    public void Render_AllLevelsExceed_ReturnsMinimalForFailsafe()
    {
        // Arrange
        ProgressiveRendering rendering = ProgressiveRenderer.Render("input", (_, _) => new string('x', 200), 100);

        // Assert — the note is appended but the output still exceeds the limit; ResponseTruncator finishes.
        rendering.Level.ShouldBe(DetailLevel.Minimal);
        rendering.Text.ShouldContain("--- DETAIL REDUCED ---");
        rendering.Text.ShouldContain("Reduced to Minimal");
    }

    [Fact]
    public void Render_SkipsByteIdenticalLevels_ReportsCorrectLevel()
    {
        // Arrange — Full/High/Medium produce identical (too-large) output; Low is smaller and fits.
        var large = new string('x', 1000);
        var small = new string('y', 50);

        // Act
        ProgressiveRendering rendering = ProgressiveRenderer.Render(
            "input",
            (_, level) => level >= DetailLevel.Low ? small : large,
            400);

        // Assert — reports Low, not High or Medium (which were byte-identical to Full).
        rendering.Level.ShouldBe(DetailLevel.Low);
        rendering.Text.ShouldContain("Reduced to Low");
        rendering.Text.ShouldNotContain("Reduced to High");
        rendering.Text.ShouldNotContain("Reduced to Medium");
    }

    [Fact]
    public void Render_TriesLevelsInOrder()
    {
        // Arrange
        List<DetailLevel> callLog = [];

        // Act — all levels too large, so all get tried.
        ProgressiveRenderer.Render("input", (_, level) =>
        {
            callLog.Add(level);
            return new string('x', 200);
        }, 100);

        // Assert
        callLog.ShouldBe([DetailLevel.Full, DetailLevel.High, DetailLevel.Medium, DetailLevel.Low, DetailLevel.Minimal]);
    }

    [Fact]
    public void Render_OutputFitsButNoteWouldNot_FallsToNextLevel()
    {
        // Arrange — High's raw output fits the 200-char budget on its own but not once the reduction note
        // is appended; Medium fits including its note. Returning High would hand the downstream truncator
        // an over-budget string — exactly the mid-chop this renderer exists to prevent.
        ProgressiveRendering rendering = ProgressiveRenderer.Render(
            "input",
            (_, level) => level switch
            {
                DetailLevel.Full => new string('F', 500),
                DetailLevel.High => new string('H', 190),
                _ => new string('M', 20)
            },
            200);

        // Assert
        rendering.Level.ShouldBe(DetailLevel.Medium);
        rendering.Text.Length.ShouldBeLessThanOrEqualTo(200);
        rendering.Text.ShouldNotContain(new string('H', 190));
        rendering.Text.ShouldContain("Reduced to Medium");
    }

    [Fact]
    public void Render_EqualLengthDistinctLevels_ReturnsLowestLevelNotStaleEarlierLevel()
    {
        // Arrange — every level produces distinct 20-char content; none fits the 10-char limit. Identical
        // lengths across levels would fool a length-based skip into returning a stale earlier level's content
        // under Minimal's label. Content comparison must avoid that.
        ProgressiveRendering rendering = ProgressiveRenderer.Render("input", (_, level) => level switch
        {
            DetailLevel.Full => new string('F', 20),
            DetailLevel.High => new string('H', 20),
            DetailLevel.Medium => new string('M', 20),
            DetailLevel.Low => new string('L', 20),
            _ => new string('N', 20)
        }, 10);

        // Assert — returned content is the Minimal level (matching the note's label), not stale Full.
        rendering.Level.ShouldBe(DetailLevel.Minimal);
        rendering.Text.ShouldContain("Reduced to Minimal");
        rendering.Text.ShouldContain(new string('N', 20));
        rendering.Text.ShouldNotContain(new string('F', 20));
    }

    [Fact]
    public void Render_CustomDescribeReduction_UsedInNote()
    {
        // Act — a domain can explain its own reduction; the note carries that text instead of the default.
        string result = ProgressiveRenderer.Render(
            "input",
            FullOverflows,
            400,
            level => $"custom reduction note for {level}").Text;

        // Assert
        result.ShouldContain("custom reduction note for High");
    }

    [Fact]
    public void Render_EmptyDescription_EmitsNoNoteAtAll()
    {
        // Arrange — the domain's way of saying this result lost nothing at this level. Only it can know:
        // below a cap the level above was never rendered, and rendering it to compare is the work the cap
        // exists to avoid.
        string result = ProgressiveRenderer.Render(
            "input", (_, _) => "No issues found.", 400, _ => "", DetailLevel.Minimal).Text;

        // Assert — not merely a note with an empty tail: the whole marker goes, because an agent matches on
        // it to know the response is a reduction.
        result.ShouldBe("No issues found.");
        result.ShouldNotContain("DETAIL REDUCED");
    }

    [Fact]
    public void Render_EmptyDescriptionAndNothingFits_StillEmitsNoNote()
    {
        // Arrange — the failsafe path takes the same branch. Nothing was reduced, so nothing is announced,
        // and ResponseTruncator's own footer reports the cut if there is one.
        string result = ProgressiveRenderer.Render(
            "input", (_, _) => new string('x', 200), 100, _ => "").Text;

        // Assert
        result.ShouldBe(new string('x', 200));
        result.ShouldNotContain("DETAIL REDUCED");
    }

    [Fact]
    public void Render_StartLevelBelowFull_NeverRendersTheLevelsAboveIt()
    {
        // Arrange
        List<DetailLevel> callLog = [];

        // Act — every level exceeds the limit, so every level the walk is willing to try gets tried.
        ProgressiveRenderer.Render(
            "input",
            (_, level) =>
            {
                callLog.Add(level);
                return new string('x', 200);
            },
            100,
            startLevel: DetailLevel.Medium);

        // Assert — a cap skips the work, rather than doing it and discarding the result. Formatting a
        // solution-wide result at Full is the expensive step a caller asking for a rollup is avoiding.
        callLog.ShouldBe([DetailLevel.Medium, DetailLevel.Low, DetailLevel.Minimal]);
    }

    [Fact]
    public void Render_StartLevelFits_NoteSaysItWasRequestedRatherThanForced()
    {
        // Act — Low renders well inside the budget, so nothing overflowed.
        string result = ProgressiveRenderer.Render(
            "input", (_, _) => new string('y', 50), 400, startLevel: DetailLevel.Low).Text;

        // Assert — the marker stays the one anchor across both leads; claiming the budget forced this
        // would be false, and would send a caller off to narrow a scan that never overflowed.
        result.ShouldContain("--- DETAIL REDUCED ---");
        result.ShouldContain("Rendered at the requested detail level Low");
        result.ShouldNotContain("character limit");
    }

    [Fact]
    public void Render_StartLevelDoesNotFit_StepsBelowItAndNamesTheCharacterLimit()
    {
        // Act — a cap is not a floor: Low is over the budget, so the walk keeps going.
        string result = ProgressiveRenderer.Render(
            "input",
            (_, level) => level == DetailLevel.Minimal ? new string('y', 50) : new string('x', 1000),
            400,
            startLevel: DetailLevel.Low).Text;

        // Assert — below the cap the budget is what decided, so the note names the character limit as an
        // uncapped render's does.
        result.ShouldContain("Output exceeded the 400 character limit. Reduced to Minimal");
        result.ShouldNotContain("requested detail level");
    }

    [Fact]
    public void Render_StartLevelFullAndFullFits_ReturnsItVerbatimWithNoNote()
    {
        // Arrange
        var output = new string('x', 50);

        // Act — the explicit Full is the whole test, so it must survive cleanup: stripped as a redundant
        // default, this becomes a duplicate of Render_FullWithinTheBudget_ReturnsItVerbatim and silently stops
        // pinning anything about the cap.
        // ReSharper disable once RedundantArgumentDefaultValue
        string result = ProgressiveRenderer.Render("input", (_, _) => output, 100, startLevel: DetailLevel.Full).Text;

        // Assert — Full is returned verbatim whether or not it was asked for. That is what keeps the
        // requested-level lead unreachable on the default path, and the default response byte-identical.
        result.ShouldBe(output);
        result.ShouldNotContain("DETAIL REDUCED");
    }

    [Fact]
    public void Render_StartLevelIsTheLastLevelAndNothingFits_FailsafeNamesItRatherThanFull()
    {
        // Arrange — with Minimal capped, no level above it is ever rendered, so a failsafe still seeding
        // its level at Full would label the returned content with a level nothing produced.
        ProgressiveRendering rendering = ProgressiveRenderer.Render(
            "input", (_, _) => new string('x', 200), 100, startLevel: DetailLevel.Minimal);

        // Assert — it genuinely is the level asked for; ResponseTruncator's own footer reports the cut.
        rendering.Level.ShouldBe(DetailLevel.Minimal);
        rendering.Text.ShouldContain("Rendered at the requested detail level Minimal");
        rendering.Text.ShouldNotContain("Reduced to Full");
    }

    /// <summary>Full overflows a 400-character budget; every lower level renders 50 characters.</summary>
    private static string FullOverflows(string _, DetailLevel level)
    {
        return level == DetailLevel.Full ? new string('x', 1000) : new string('y', 50);
    }
}