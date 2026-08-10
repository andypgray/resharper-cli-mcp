using System.Text;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Sarif;

namespace Zphil.ReSharperCli.Tests.Formatting;

/// <summary>
///     The authoritative string spec for the inspection listing. One fixture — 11 issues over 3 files,
///     hitting every branch of the ladder — is rendered at each <see cref="DetailLevel" /> and pinned with
///     an exact <c>ShouldBe</c>. Two properties earn their own facts because they are what makes the ladder
///     cheap: with no rule repeating inside a file, High is byte-identical to Full, and with fewer files
///     than the cap, Medium is byte-identical to High, so <see cref="ProgressiveRenderer" />'s
///     content-equality skip drops through the redundant level at no cost. Output uses <c>\n</c> line
///     endings and is ASCII-only.
/// </summary>
public sealed class IssueMarkdownFormatterTests
{
    private const string OrderPath = @"C:\repo\src\Dto\Order.cs";
    private const string ControllerPath = @"C:\repo\src\Api\OrdersController.cs";
    private const string RepositoryPath = @"C:\repo\src\Data\OrderRepository.cs";

    private const string RedundantUsingMessage =
        "Using directive is not required by the code and can be safely removed";

    private const string NullReferenceMessage = "Possible 'System.NullReferenceException'";

    [Fact]
    public void Format_NoIssues_ReturnsNoIssuesFoundLiteral()
    {
        // Act
        string result = IssueMarkdownFormatter.Format([], DetailLevel.Full);

        // Assert
        result.ShouldBe("No issues found.");
    }

    [Fact]
    public void Format_NoIssues_ReturnsTheSameLiteralAtEveryLevel()
    {
        // Act / Assert — looped in a [Fact] rather than a [Theory] because the internal DetailLevel cannot
        // appear in a public test method's signature (CS0051).
        foreach (DetailLevel level in Enum.GetValues<DetailLevel>())
            IssueMarkdownFormatter.Format([], level).ShouldBe("No issues found.");
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
        string result = IssueMarkdownFormatter.Format(issues, DetailLevel.Full);

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
        string result = IssueMarkdownFormatter.Format(issues, DetailLevel.Full);

        // Assert — the byte-compatibility pin: Full is character for character what this tool returned
        // before the ladder existed, so a scoped scan is unchanged.
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
        string result = IssueMarkdownFormatter.Format(issues, DetailLevel.Full);

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
        // Act / Assert — StringBuilder.AppendLine would emit \r\n here on Windows; every level joins with \n.
        foreach (DetailLevel level in Enum.GetValues<DetailLevel>())
        {
            string result = IssueMarkdownFormatter.Format(Mixed(), level);
            result.ShouldNotContain("\r\n");
            Ascii.IsValid(result).ShouldBeTrue(); // no em dash or other non-ASCII sneaking in from roz's shapes
        }
    }

    [Fact]
    public void Format_EveryLevel_StartsWithTheSameFoundHeader()
    {
        // Act / Assert — the header is invariant at every level (Minimal swaps the ':' for a '.'), so a
        // caller can anchor on it without knowing which level fired.
        foreach (DetailLevel level in Enum.GetValues<DetailLevel>())
            IssueMarkdownFormatter.Format(Mixed(), level)
                .ShouldStartWith("Found 11 issue(s) across 3 file(s)");
    }

    [Fact]
    public void Format_Full_ListsEveryIssueOnItsOwnLine()
    {
        // Act
        string result = IssueMarkdownFormatter.Format(Mixed(), DetailLevel.Full);

        // Assert
        result.ShouldBe(
            "Found 11 issue(s) across 3 file(s):\n"
            + "\n"
            + $"### {OrderPath}\n"
            + "- **Line 13** [WARNING] `NotAccessedPositionalProperty.Global`: Positional property 'CustomerReference' is never used\n"
            + "- **Line 14** [WARNING] `NotAccessedPositionalProperty.Global`: Positional property 'PlacedOn' is never used\n"
            + "- **Line 15** [WARNING] `NotAccessedPositionalProperty.Global`: Positional property 'Total' is never used\n"
            + "- **Line 20** [WARNING] `NotAccessedPositionalProperty.Global`: Positional property 'Notes' is never used\n"
            + "- **Line 40** [SUGGESTION] `UnusedMember.Global`: Method 'Total' is never used\n"
            + "\n"
            + $"### {ControllerPath}\n"
            + $"- **Line 1** [WARNING] `RedundantUsingDirective`: {RedundantUsingMessage}\n"
            + $"- **Line 2** [WARNING] `RedundantUsingDirective`: {RedundantUsingMessage}\n"
            + $"- **Line 3** [WARNING] `RedundantUsingDirective`: {RedundantUsingMessage}\n"
            + "\n"
            + $"### {RepositoryPath}\n"
            + "- **Line 12** [ERROR] `.CSharpErrors`: Cannot resolve symbol 'OrderDto'\n"
            + $"- **Line 5** [WARNING] `PossibleNullReferenceException`: {NullReferenceMessage}\n"
            + $"- **Line 5** [WARNING] `PossibleNullReferenceException`: {NullReferenceMessage}\n");
    }

    [Fact]
    public void Format_High_CollapsesRepeatedRulesWithinAFileToOneLine()
    {
        // Act
        string result = IssueMarkdownFormatter.Format(Mixed(), DetailLevel.High);

        // Assert — a run and a stray line compress to "13-15, 20"; the group's messages differ so the child
        // is an example, while RedundantUsingDirective's three identical messages render bare (lossless);
        // the two issues sharing line 5 dedupe in the range list while x2 keeps the true count.
        result.ShouldBe(
            "Found 11 issue(s) across 3 file(s):\n"
            + "\n"
            + $"### {OrderPath}\n"
            + "- **`NotAccessedPositionalProperty.Global`** [WARNING] x4, lines 13-15, 20\n"
            + "  - e.g. Positional property 'CustomerReference' is never used\n"
            + "- **Line 40** [SUGGESTION] `UnusedMember.Global`: Method 'Total' is never used\n"
            + "\n"
            + $"### {ControllerPath}\n"
            + "- **`RedundantUsingDirective`** [WARNING] x3, lines 1-3\n"
            + $"  - {RedundantUsingMessage}\n"
            + "\n"
            + $"### {RepositoryPath}\n"
            + "- **Line 12** [ERROR] `.CSharpErrors`: Cannot resolve symbol 'OrderDto'\n"
            + "- **`PossibleNullReferenceException`** [WARNING] x2, lines 5\n"
            + $"  - {NullReferenceMessage}\n");
    }

    [Fact]
    public void Format_Medium_FewerFilesThanTheCap_IsByteIdenticalToHigh()
    {
        // The free-skip property: with 3 files against a cap of 8, Medium has nothing to omit, so
        // ProgressiveRenderer's content-equality check skips the level rather than reporting a reduction
        // that changed nothing. Holds only because Medium selects by count but emits in first-seen order.
        IssueMarkdownFormatter.Format(Mixed(), DetailLevel.Medium)
            .ShouldBe(IssueMarkdownFormatter.Format(Mixed(), DetailLevel.High));
    }

    [Fact]
    public void Format_Medium_MoreFilesThanTheCap_ListsTheMostAffectedInFirstSeenOrder()
    {
        // Act
        string result = IssueMarkdownFormatter.Format(NineFiles(), DetailLevel.Medium);

        // Assert — F9 is seen last but is the most affected, so it survives the top-8 selection and still
        // renders last; F8, a singleton beyond the cap, is collapsed to the trailing count.
        result.ShouldBe(
            "Found 10 issue(s) across 9 file(s):\n"
            + "\n"
            + "### src/F1.cs\n"
            + "- **Line 1** [WARNING] `R`: m1\n"
            + "\n"
            + "### src/F2.cs\n"
            + "- **Line 2** [WARNING] `R`: m2\n"
            + "\n"
            + "### src/F3.cs\n"
            + "- **Line 3** [WARNING] `R`: m3\n"
            + "\n"
            + "### src/F4.cs\n"
            + "- **Line 4** [WARNING] `R`: m4\n"
            + "\n"
            + "### src/F5.cs\n"
            + "- **Line 5** [WARNING] `R`: m5\n"
            + "\n"
            + "### src/F6.cs\n"
            + "- **Line 6** [WARNING] `R`: m6\n"
            + "\n"
            + "### src/F7.cs\n"
            + "- **Line 7** [WARNING] `R`: m7\n"
            + "\n"
            + "### src/F9.cs\n"
            + "- **`R`** [WARNING] x2, lines 9, 20\n"
            + "  - e.g. m9\n"
            + "\n"
            + "  (+1 file(s) with 1 issue(s), not listed)");
    }

    [Fact]
    public void Format_Low_ReplacesTheListingWithRuleAndFileRollups()
    {
        // Act
        string result = IssueMarkdownFormatter.Format(Mixed(), DetailLevel.Low);

        // Assert — rules rank by count alone (never severity first), ties break on rule id; files rank by
        // count with first-seen keeping the two 3-issue files in source order.
        result.ShouldBe(
            "Found 11 issue(s) across 3 file(s):\n"
            + "\n"
            + "By rule (5 of 5):\n"
            + "  `NotAccessedPositionalProperty.Global` [WARNING]: 4 issue(s) in 1 file(s)\n"
            + "  `RedundantUsingDirective` [WARNING]: 3 issue(s) in 1 file(s)\n"
            + "  `PossibleNullReferenceException` [WARNING]: 2 issue(s) in 1 file(s)\n"
            + "  `.CSharpErrors` [ERROR]: 1 issue(s) in 1 file(s)\n"
            + "  `UnusedMember.Global` [SUGGESTION]: 1 issue(s) in 1 file(s)\n"
            + "\n"
            + "By file (3 of 3):\n"
            + $"  {OrderPath}: 4 WARNING, 1 SUGGESTION\n"
            + $"  {ControllerPath}: 3 WARNING\n"
            + $"  {RepositoryPath}: 1 ERROR, 2 WARNING");
    }

    [Fact]
    public void Format_Minimal_IsSingleLineOfTotalsSeveritiesAndTopRules()
    {
        // Act
        string result = IssueMarkdownFormatter.Format(Mixed(), DetailLevel.Minimal);

        // Assert
        result.ShouldBe(
            "Found 11 issue(s) across 3 file(s). 1 ERROR, 9 WARNING, 1 SUGGESTION. "
            + "Top rules: `NotAccessedPositionalProperty.Global` x4, `RedundantUsingDirective` x3, "
            + "`PossibleNullReferenceException` x2 (+2 rule(s) not listed).");
    }

    [Fact]
    public void Format_High_NoRuleRepeatsWithinAFile_IsByteIdenticalToFull()
    {
        // Arrange — the scoped-scan shape: every issue a distinct rule.
        List<InspectIssue> issues =
        [
            new("A.cs", 1, null, "WARNING", "Rule1", "msg one"),
            new("A.cs", 5, null, "ERROR", "Rule2", "msg two"),
            new("B.cs", 9, null, "SUGGESTION", "Rule3", "msg three")
        ];

        // Act / Assert — nothing to collapse, so High costs nothing and ProgressiveRenderer skips it.
        IssueMarkdownFormatter.Format(issues, DetailLevel.High)
            .ShouldBe(IssueMarkdownFormatter.Format(issues, DetailLevel.Full));
    }

    [Fact]
    public void Format_LowerLevelsAreMonotonicallySmaller()
    {
        // Arrange — the motivating solution-wide shape.
        IReadOnlyList<InspectIssue> issues = SolutionWideRepetition();

        // Act
        var lengths = Enum.GetValues<DetailLevel>()
            .Select(level => IssueMarkdownFormatter.Format(issues, level).Length)
            .ToList();

        // Assert — a level that accidentally grows would make ProgressiveRenderer's walk pointless.
        lengths.ShouldBe(lengths.OrderByDescending(length => length).ToList());
    }

    [Fact]
    public void Render_SolutionWideRepetition_SelectsHighUnderTheDefaultBudget()
    {
        // Arrange — the run that motivated the ladder: 150 issues over 24 files, 120 of them one rule, at
        // realistic absolute-path and message lengths, against the default 25,000-character budget.
        IReadOnlyList<InspectIssue> issues = SolutionWideRepetition();
        const int maxChars = 25_000;
        string full = IssueMarkdownFormatter.Format(issues, DetailLevel.Full);

        // Act
        string result = ProgressiveRenderer.Render(
            issues, IssueMarkdownFormatter.Format, maxChars, IssueMarkdownFormatter.DescribeReduction);

        // Assert — this pins the size arithmetic: if a format change pushes High past the budget, this
        // fails loudly instead of silently degrading every solution-wide run to Medium.
        full.Length.ShouldBeGreaterThan(maxChars); // precondition: Full genuinely did not fit
        result.Length.ShouldBeLessThanOrEqualTo(maxChars); // note included, so the budget genuinely holds
        result.ShouldStartWith("Found 150 issue(s) across 24 file(s):"); // header intact, no mid-line chop
        result.ShouldContain("--- DETAIL REDUCED ---");
        result.ShouldContain("Reduced to High");
        result.ShouldContain("x30, lines "); // the repeated rule collapsed rather than filling the budget
    }

    [Theory]
    [InlineData("13,14,15", "13-15")] // a run collapses
    [InlineData("13,15", "13, 15")] // a gap does not
    [InlineData("3,1,2", "1-3")] // input order is irrelevant
    [InlineData("5,5", "5")] // two issues on one line dedupe; the x{n} count carries the true total
    [InlineData("7", "7")]
    [InlineData("1,2,4,5,6,9", "1-2, 4-6, 9")]
    public void FormatLineRanges_CollapsesRunsAndSortsAscending(string input, string expected)
    {
        // Arrange — string in, string out: the internal InspectIssue never reaches the public signature.
        var lines = input.Split(',').Select(int.Parse);

        // Act / Assert
        IssueMarkdownFormatter.FormatLineRanges(lines).ShouldBe(expected);
    }

    [Fact]
    public void FormatLineRanges_MoreRangesThanTheCap_ListsTwelveThenCountsTheRest()
    {
        // Arrange — 30 alternating lines never collapse, so each is its own range.
        var lines = Enumerable.Range(0, 30).Select(i => 1 + i * 2).ToList();

        // Act
        string result = IssueMarkdownFormatter.FormatLineRanges(lines);

        // Assert
        result.ShouldBe("1, 3, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23 (+18 more)");
    }

    [Fact]
    public void DescribeReduction_LevelsThatDropContent_CarryTheNarrowingHint()
    {
        // Act / Assert
        IssueMarkdownFormatter.DescribeReduction(DetailLevel.Medium)
            .ShouldEndWith(IssueMarkdownFormatter.NarrowingHint);
        IssueMarkdownFormatter.DescribeReduction(DetailLevel.Low)
            .ShouldEndWith(IssueMarkdownFormatter.NarrowingHint);
        IssueMarkdownFormatter.DescribeReduction(DetailLevel.Minimal)
            .ShouldEndWith(IssueMarkdownFormatter.NarrowingHint);
    }

    [Fact]
    public void DescribeReduction_High_OmitsTheNarrowingHintAndSaysNothingWasLost()
    {
        // Act
        string description = IssueMarkdownFormatter.DescribeReduction(DetailLevel.High);

        // Assert — High is the level essentially every solution-wide run lands on, and it drops no issue,
        // so telling the agent to re-run scoped there would be noise. The count reassurance is the one
        // honest distinction between a reduced result and a truncated one.
        description.ShouldNotContain("Narrow the scan");
        description.ShouldContain("Every issue is still counted.");
    }

    /// <summary>
    ///     11 issues over 3 files, hitting every branch: four issues of one rule with differing messages on
    ///     a run plus a stray line, a singleton inside that collapsing file, three issues of one rule with
    ///     identical messages, one ERROR for the severity ordering, and two issues sharing a line.
    /// </summary>
    private static List<InspectIssue> Mixed()
    {
        return
        [
            new InspectIssue(OrderPath, 13, null, "WARNING", "NotAccessedPositionalProperty.Global",
                "Positional property 'CustomerReference' is never used"),
            new InspectIssue(OrderPath, 14, null, "WARNING", "NotAccessedPositionalProperty.Global",
                "Positional property 'PlacedOn' is never used"),
            new InspectIssue(OrderPath, 15, null, "WARNING", "NotAccessedPositionalProperty.Global",
                "Positional property 'Total' is never used"),
            new InspectIssue(OrderPath, 20, null, "WARNING", "NotAccessedPositionalProperty.Global",
                "Positional property 'Notes' is never used"),
            new InspectIssue(OrderPath, 40, null, "SUGGESTION", "UnusedMember.Global", "Method 'Total' is never used"),
            new InspectIssue(ControllerPath, 1, null, "WARNING", "RedundantUsingDirective", RedundantUsingMessage),
            new InspectIssue(ControllerPath, 2, null, "WARNING", "RedundantUsingDirective", RedundantUsingMessage),
            new InspectIssue(ControllerPath, 3, null, "WARNING", "RedundantUsingDirective", RedundantUsingMessage),
            new InspectIssue(RepositoryPath, 12, null, "ERROR", ".CSharpErrors", "Cannot resolve symbol 'OrderDto'"),
            new InspectIssue(RepositoryPath, 5, null, "WARNING", "PossibleNullReferenceException", NullReferenceMessage),
            new InspectIssue(RepositoryPath, 5, null, "WARNING", "PossibleNullReferenceException", NullReferenceMessage)
        ];
    }

    /// <summary>
    ///     Nine files, one issue each except the last-seen, which has two. Past Medium's cap of eight, so the
    ///     top-8 selection and the first-seen emission order are both observable.
    /// </summary>
    private static List<InspectIssue> NineFiles()
    {
        List<InspectIssue> issues = [];
        for (var i = 1; i <= 9; i++) issues.Add(new InspectIssue($"src/F{i}.cs", i, null, "WARNING", "R", $"m{i}"));

        issues.Add(new InspectIssue("src/F9.cs", 20, null, "WARNING", "R", "m9b"));
        return issues;
    }

    /// <summary>
    ///     The motivating shape: 120 issues of one rule over 4 generated DTO files (each message naming a
    ///     different property), plus 30 singletons over 20 more files, at realistic absolute-path and message
    ///     lengths. 150 issues over 24 files — the run that overflowed a 25,000-character budget at Full.
    /// </summary>
    private static List<InspectIssue> SolutionWideRepetition()
    {
        List<InspectIssue> issues = [];

        for (var file = 0; file < 4; file++)
        {
            var path =
                $@"C:\src\Contoso.Ordering\src\Contoso.Ordering.Contracts\Generated\Dto\OrderContractDto{file:D2}.cs";
            for (var i = 0; i < 30; i++)
                issues.Add(new InspectIssue(
                    path,
                    13 + i,
                    null,
                    "WARNING",
                    "NotAccessedPositionalProperty.Global",
                    $"Positional property 'CustomerReferenceNumber{i:D2}' is never used anywhere in the solution "
                    + "and can safely be removed from the record declaration"));
        }

        for (var file = 0; file < 20; file++)
        {
            var path =
                $@"C:\src\Contoso.Ordering\src\Contoso.Ordering.Application\Handlers\Orders\PlaceOrderCommandHandler{file:D2}.cs";

            // Distinct rule ids keep these singletons: 30 issues that High has nothing to collapse.
            for (var i = 0; i < (file < 10 ? 2 : 1); i++)
                issues.Add(new InspectIssue(
                    path,
                    40 + i * 7,
                    null,
                    "SUGGESTION",
                    $"UnusedMember.Global{i}",
                    $"Method 'CalculateOrderTotalWithDiscounts{file:D2}{i}' is never used and can be removed "
                    + "from the handler"));
        }

        return issues;
    }
}