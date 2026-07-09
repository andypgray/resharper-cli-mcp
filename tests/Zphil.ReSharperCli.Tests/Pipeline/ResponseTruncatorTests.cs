using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Pipeline;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Tests.Pipeline;

public sealed class ResponseTruncatorTests
{
    [Fact]
    public void ComputeMaxChars_NullValue_ReturnsDefault()
    {
        // Act
        int result = ResponseTruncator.ComputeMaxChars(null);

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
        string result = ResponseTruncator.TruncateIfNeeded(text, ResharperTools.InspectToolName, 100);

        // Assert
        result.ShouldBe(text);
    }

    [Fact]
    public void TruncateIfNeeded_TextExceedsLimit_CutsAtLastNewlineBeforeCap()
    {
        // Arrange — a newline sits at index 5 and index 11; the cap falls at 12.
        const string text = "line1\nline2\nline3-and-a-long-tail-past-the-cap";

        // Act
        string result = ResponseTruncator.TruncateIfNeeded(text, null, 12);

        // Assert
        result.ShouldStartWith("line1\nline2\n\n--- RESPONSE TRUNCATED ---");
    }

    [Fact]
    public void TruncateIfNeeded_NoNewlineBeforeCap_CutsAtCap()
    {
        // Arrange
        const string text = "abcdefghijklmnopqrstuvwxyz";

        // Act
        string result = ResponseTruncator.TruncateIfNeeded(text, null, 8);

        // Assert
        result.ShouldStartWith("abcdefgh\n\n--- RESPONSE TRUNCATED ---");
    }

    [Fact]
    public void TruncateIfNeeded_TextExceedsLimit_FooterReportsSizeAndOmittedCount()
    {
        // Arrange
        string text = new('x', 50);

        // Act
        string result = ResponseTruncator.TruncateIfNeeded(text, null, 20);

        // Assert
        result.ShouldContain("--- RESPONSE TRUNCATED ---");
        result.ShouldContain("Output was 50 characters, limit is 20");
        result.ShouldContain("30 characters omitted");
        result.ShouldContain("The results above are incomplete.");
    }

    [Fact]
    public void TruncateIfNeeded_InspectTool_AppendsNarrowingHint()
    {
        // Arrange
        string text = new('x', 50);

        // Act
        string result = ResponseTruncator.TruncateIfNeeded(text, ResharperTools.InspectToolName, 20);

        // Assert
        result.ShouldEndWith("Narrow the scan with the files parameter or raise severity.");
    }

    [Fact]
    public void TruncateIfNeeded_NonInspectTool_NoNarrowingHint()
    {
        // Arrange
        string text = new('x', 50);

        // Act
        string result = ResponseTruncator.TruncateIfNeeded(text, ResharperTools.CleanupToolName, 20);

        // Assert
        result.ShouldEndWith("The results above are incomplete.");
        result.ShouldNotContain("Narrow the scan");
    }
}