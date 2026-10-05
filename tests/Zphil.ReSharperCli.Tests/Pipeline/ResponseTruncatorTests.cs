using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Pipeline;

namespace Zphil.ReSharperCli.Tests.Pipeline;

public sealed class ResponseTruncatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-100")]
    public void ComputeMaxChars_UnsetBlankUnparseableOrNonPositive_ReturnsDefault(string? value)
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
    public void BudgetForBody_PrefixLargerThanTheWholeBudget_StaysPositiveAndWithinTheBudget()
    {
        // Arrange — a pathological MAX_MCP_OUTPUT_TOKENS must not drive the residual negative, which would
        // print as a negative character limit in the reduction note, nor above the budget it came from.
        string prefix = new('w', 50);

        // Act
        int budget = ResponseTruncator.BudgetForBody(10, prefix);

        // Assert
        budget.ShouldBe(10);
    }

    [Fact]
    public void BudgetForBody_NoPrefix_LeavesTheBudgetExactlyAsItWas()
    {
        // A result with nothing to lead with must keep the whole budget, exactly — including under a budget
        // smaller than the floor, where rounding up would silently un-reduce an output the client cannot
        // afford.
        ResponseTruncator.BudgetForBody(25_000, "").ShouldBe(25_000);
        ResponseTruncator.BudgetForBody(100, "").ShouldBe(100);
    }
}