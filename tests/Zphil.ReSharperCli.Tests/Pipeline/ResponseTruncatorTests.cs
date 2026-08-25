using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Pipeline;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Tests.Pipeline;

public sealed class ResponseTruncatorTests
{
    [Fact]
    public void ComputeMaxChars_NullValue_ReturnsDefault()
    {
        // Act
        int result = ResponseTruncator.ComputeMaxChars((string?)null);

        // Assert
        result.ShouldBe(25_000);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-100")]
    public void ComputeMaxChars_BlankUnparseableOrNonPositive_ReturnsDefault(string value)
    {
        // Act
        int result = ResponseTruncator.ComputeMaxChars(value);

        // Assert
        result.ShouldBe(25_000);
    }

    [Theory]
    [InlineData("1000", 2_500)]
    [InlineData("4000", 10_000)]
    public void ComputeMaxChars_PositiveTokenBudget_ReturnsTokensTimesCharsPerToken(string value, int expected)
    {
        // Act
        int result = ResponseTruncator.ComputeMaxChars(value);

        // Assert
        result.ShouldBe(expected);
    }

    [Fact]
    public void TruncateIfNeeded_TextWithinLimit_ReturnsUnchanged()
    {
        // Arrange
        const string text = "short output";

        // Act
        string result = ResponseTruncator.TruncateIfNeeded(text, IssueMarkdownFormatter.NarrowingHint, 100);

        // Assert
        result.ShouldBe(text);
    }

    [Fact]
    public void TruncateIfNeeded_TextExceedsLimit_CutsAtLastNewlineBeforeCap()
    {
        // Arrange — a newline sits at index 5 and index 11; the cap falls at 12.
        const string text = "line1\nline2\nline3-and-a-long-tail-past-the-cap";

        // Act
        string result = ResponseTruncator.TruncateIfNeeded(text, "", 12);

        // Assert
        result.ShouldStartWith("line1\nline2\n\n--- RESPONSE TRUNCATED ---");
    }

    [Fact]
    public void TruncateIfNeeded_NoNewlineBeforeCap_CutsAtCap()
    {
        // Arrange
        const string text = "abcdefghijklmnopqrstuvwxyz";

        // Act
        string result = ResponseTruncator.TruncateIfNeeded(text, "", 8);

        // Assert
        result.ShouldStartWith("abcdefgh\n\n--- RESPONSE TRUNCATED ---");
    }

    [Fact]
    public void TruncateIfNeeded_TextExceedsLimit_FooterReportsSizeAndOmittedCount()
    {
        // Arrange
        string text = new('x', 50);

        // Act
        string result = ResponseTruncator.TruncateIfNeeded(text, "", 20);

        // Assert
        result.ShouldContain("--- RESPONSE TRUNCATED ---");
        result.ShouldContain("Output was 50 characters, limit is 20");
        result.ShouldContain("30 characters omitted");
        result.ShouldContain("The results above are incomplete.");
    }

    [Fact]
    public void TruncateIfNeeded_WithHint_AppendsItAfterTheFooter()
    {
        // Arrange
        string text = new('x', 50);

        // Act
        string result = ResponseTruncator.TruncateIfNeeded(text, IssueMarkdownFormatter.NarrowingHint, 20);

        // Assert — the verbatim spec, plus the shared-const relationship: the same remedy reaches an agent
        // from a truncation footer and from a progressive-reduction note, in one spelling.
        result.ShouldEndWith("Narrow the scan with the files parameter or raise severity.");
        result.ShouldEndWith(IssueMarkdownFormatter.NarrowingHint);
    }

    [Fact]
    public void TruncateIfNeeded_EmptyHint_FooterEndsAtIncomplete()
    {
        // Arrange
        string text = new('x', 50);

        // Act
        string result = ResponseTruncator.TruncateIfNeeded(text, "", 20);

        // Assert
        result.ShouldEndWith("The results above are incomplete.");
    }

    [Fact]
    public void TruncationHintFor_KnownTools_MapEachToItsOwnRemedy()
    {
        // Assert — inspect's remedy is a narrower next scan or a report file; cleanup's and reset's are the
        // reassurance that the work was done in full, so a chopped report cannot read as chopped work.
        ResharperTools.TruncationHintFor(ResharperTools.InspectToolName).ShouldBe(IssueMarkdownFormatter.TruncationRemedy);
        ResharperTools.TruncationHintFor(ResharperTools.CleanupToolName).ShouldBe(CleanupSummaryFormatter.CleanupRanInFull);
        ResharperTools.TruncationHintFor(ResharperTools.ResetCacheToolName).ShouldBe(CacheResetFormatter.ResetRanInFull);
    }

    [Fact]
    public void TruncationHintFor_UnknownTool_ReturnsEmpty()
    {
        // Assert
        ResharperTools.TruncationHintFor("some_other_tool").ShouldBe("");
        ResharperTools.TruncationHintFor(null).ShouldBe("");
    }
}