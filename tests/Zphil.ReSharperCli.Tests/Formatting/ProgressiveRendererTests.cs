using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Formatting;

namespace Zphil.ReSharperCli.Tests.Formatting;

/// <summary>
///     Ported from roz's <c>ProgressiveRendererTests</c>: the renderer steps down through the ordered
///     <see cref="DetailLevel" />s until the output fits, returns <see cref="DetailLevel.Full" /> verbatim
///     when it fits, appends a <c>--- DETAIL REDUCED ---</c> note otherwise (the note counts toward the fit
///     check), skips byte-identical levels (by content, not length), and falls back to the smallest
///     rendering plus the note when nothing fits so the char-level truncator can finish the job.
/// </summary>
public sealed class ProgressiveRendererTests
{
    [Fact]
    public void Render_FitsAtFull_NoReductionNote()
    {
        // Arrange
        var output = new string('x', 50);

        // Act
        string result = ProgressiveRenderer.Render("input", (_, _) => output, 100);

        // Assert
        result.ShouldBe(output);
        result.ShouldNotContain("DETAIL REDUCED");
    }

    [Fact]
    public void Render_ExactlyAtLimit_NoReductionNote()
    {
        // Arrange
        var output = new string('x', 100);

        // Act
        string result = ProgressiveRenderer.Render("input", (_, _) => output, 100);

        // Assert
        result.ShouldBe(output);
        result.ShouldNotContain("DETAIL REDUCED");
    }

    [Fact]
    public void Render_FitsAtHigh_AppendsReductionNoteNamingLevel()
    {
        // Arrange — Full is too large; High plus its reduction note fits.
        List<DetailLevel> callLog = [];

        // Act
        string result = ProgressiveRenderer.Render("input", (_, level) =>
        {
            callLog.Add(level);
            return level == DetailLevel.Full ? new string('x', 1000) : new string('y', 50);
        }, 400);

        // Assert
        result.ShouldContain("--- DETAIL REDUCED ---");
        result.ShouldContain("Reduced to High");
        callLog.ShouldContain(DetailLevel.Full);
        callLog.ShouldContain(DetailLevel.High);
    }

    [Fact]
    public void Render_FitsAtLow_NamesLowLevel()
    {
        // Arrange — Full, High, Medium all too large; Low plus its note fits.
        string result = ProgressiveRenderer.Render(
            "input",
            (_, level) => level < DetailLevel.Low ? new string('x', 1000) : new string('y', 50),
            400);

        // Assert
        result.ShouldContain("Reduced to Low");
    }

    [Fact]
    public void Render_AllLevelsExceed_ReturnsMinimalForFailsafe()
    {
        // Arrange — every level exceeds the limit.
        string result = ProgressiveRenderer.Render("input", (_, _) => new string('x', 200), 100);

        // Assert — the note is appended but the output still exceeds the limit; ResponseTruncator finishes.
        result.ShouldContain("--- DETAIL REDUCED ---");
        result.ShouldContain("Reduced to Minimal");
    }

    [Fact]
    public void Render_SkipsByteIdenticalLevels_ReportsCorrectLevel()
    {
        // Arrange — Full/High/Medium produce identical (too-large) output; Low is smaller and fits.
        var large = new string('x', 1000);
        var small = new string('y', 50);

        // Act
        string result = ProgressiveRenderer.Render(
            "input",
            (_, level) => level >= DetailLevel.Low ? small : large,
            400);

        // Assert — reports Low, not High or Medium (which were byte-identical to Full).
        result.ShouldContain("Reduced to Low");
        result.ShouldNotContain("Reduced to High");
        result.ShouldNotContain("Reduced to Medium");
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
    public void Render_ReductionNoteIncludesCharLimit()
    {
        // Act
        string result = ProgressiveRenderer.Render(
            "input",
            (_, level) => level == DetailLevel.Full ? new string('x', 1000) : new string('y', 50),
            400);

        // Assert
        result.ShouldContain("400 character limit");
    }

    [Fact]
    public void Render_OutputFitsButNoteWouldNot_FallsToNextLevel()
    {
        // Arrange — High's raw output fits the 200-char budget on its own but not once the reduction note
        // is appended; Medium fits including its note. Returning High would hand the downstream truncator
        // an over-budget string — exactly the mid-chop this renderer exists to prevent.
        string result = ProgressiveRenderer.Render(
            "input",
            (_, level) => level switch
            {
                DetailLevel.Full => new string('F', 500),
                DetailLevel.High => new string('H', 190),
                _ => new string('M', 20)
            },
            200);

        // Assert
        result.Length.ShouldBeLessThanOrEqualTo(200);
        result.ShouldNotContain(new string('H', 190));
        result.ShouldContain("Reduced to Medium");
    }

    [Fact]
    public void Render_EqualLengthDistinctLevels_ReturnsLowestLevelNotStaleEarlierLevel()
    {
        // Arrange — every level produces distinct 20-char content; none fits the 10-char limit. Identical
        // lengths across levels would fool a length-based skip into returning a stale earlier level's content
        // under Minimal's label. Content comparison must avoid that.
        string result = ProgressiveRenderer.Render("input", (_, level) => level switch
        {
            DetailLevel.Full => new string('F', 20),
            DetailLevel.High => new string('H', 20),
            DetailLevel.Medium => new string('M', 20),
            DetailLevel.Low => new string('L', 20),
            _ => new string('N', 20)
        }, 10);

        // Assert — returned content is the Minimal level (matching the note's label), not stale Full.
        result.ShouldContain("Reduced to Minimal");
        result.ShouldContain(new string('N', 20));
        result.ShouldNotContain(new string('F', 20));
    }

    [Fact]
    public void Render_CustomDescribeReduction_UsedInNote()
    {
        // Act — a domain can explain its own reduction; the note carries that text instead of the default.
        string result = ProgressiveRenderer.Render(
            "input",
            (_, level) => level == DetailLevel.Full ? new string('x', 1000) : new string('y', 50),
            400,
            level => $"custom reduction note for {level}");

        // Assert
        result.ShouldContain("custom reduction note for High");
    }
}