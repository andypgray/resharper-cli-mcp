using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Sarif;

namespace Zphil.ReSharperCli.Tests.Formatting;

public sealed class IssueMarkdownFormatterTests
{
    [Fact]
    public void Format_NoIssues_ReturnsNoIssuesFoundLiteral()
    {
        // Act
        string result = IssueMarkdownFormatter.Format([]);

        // Assert
        result.ShouldBe("No issues found.");
    }

    [Fact]
    public void Format_Issues_HeaderCountsIssuesAndDistinctFiles()
    {
        // Arrange
        List<InspectIssue> issues =
        [
            new("A.cs", 1, null, "WARNING", "R1", "m1"),
            new("A.cs", 5, null, "ERROR", "R2", "m2"),
            new("B.cs", 9, null, "SUGGESTION", "R3", "m3")
        ];

        // Act
        string result = IssueMarkdownFormatter.Format(issues);

        // Assert
        result.ShouldStartWith("Found 3 issue(s) across 2 file(s):");
    }

    [Fact]
    public void Format_ThreeIssuesAcrossTwoFiles_ProducesExactMarkdown()
    {
        // Arrange
        List<InspectIssue> issues =
        [
            new("A.cs", 1, null, "WARNING", "Rule1", "msg one"),
            new("A.cs", 5, null, "ERROR", "Rule2", "msg two"),
            new("B.cs", 9, null, "SUGGESTION", "Rule3", "msg three")
        ];

        // Act
        string result = IssueMarkdownFormatter.Format(issues);

        // Assert
        const string expected =
            "Found 3 issue(s) across 2 file(s):\n" +
            "\n" +
            "### A.cs\n" +
            "- **Line 1** [WARNING] `Rule1`: msg one\n" +
            "- **Line 5** [ERROR] `Rule2`: msg two\n" +
            "\n" +
            "### B.cs\n" +
            "- **Line 9** [SUGGESTION] `Rule3`: msg three\n";
        result.ShouldBe(expected);
    }

    [Fact]
    public void Format_IssuesInterleavedByFile_GroupsUnderFirstSeenFileOrder()
    {
        // Arrange
        List<InspectIssue> issues =
        [
            new("First.cs", 1, null, "WARNING", "R1", "m1"),
            new("Second.cs", 2, null, "WARNING", "R2", "m2"),
            new("First.cs", 3, null, "WARNING", "R3", "m3")
        ];

        // Act
        string result = IssueMarkdownFormatter.Format(issues);

        // Assert
        result.IndexOf("### First.cs", StringComparison.Ordinal)
            .ShouldBeLessThan(result.IndexOf("### Second.cs", StringComparison.Ordinal));
        // Both First.cs issues are collected before the Second.cs heading begins.
        result.IndexOf("`R3`", StringComparison.Ordinal)
            .ShouldBeLessThan(result.IndexOf("### Second.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Format_Issues_UsesLineFeedNewlinesOnly()
    {
        // Arrange
        List<InspectIssue> issues = [new("A.cs", 1, null, "WARNING", "R1", "m1")];

        // Act
        string result = IssueMarkdownFormatter.Format(issues);

        // Assert
        result.ShouldNotContain("\r\n");
    }
}