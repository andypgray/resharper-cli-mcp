using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Sarif;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Sarif;

public sealed class SarifParserTests
{
    [Fact]
    public void Parse_TypicalReport_ReturnsAllResultsInOrder()
    {
        // Arrange
        string json = Fixtures.ReadSarif("inspect-sample.json");

        // Act
        List<InspectIssue> issues = SarifParser.Parse(json);

        // Assert
        issues.Select(i => i.RuleId).ShouldBe(
            ["RedundantUsingDirective", "UnusedField.Compiler", "UnusedMember.Global"]);
    }

    [Fact]
    public void Parse_TypicalReport_MapsFirstResultFields()
    {
        // Arrange
        string json = Fixtures.ReadSarif("inspect-sample.json");

        // Act
        InspectIssue issue = SarifParser.Parse(json)[0];

        // Assert
        issue.Line.ShouldBe(1);
        issue.EndLine.ShouldBe(1);
        issue.Severity.ShouldBe("WARNING");
        issue.RuleId.ShouldBe("RedundantUsingDirective");
        issue.Message.ShouldBe("Using directive is not required by the code and can be safely removed");
    }

    [Fact]
    public void Parse_FileUri_ConvertsToLocalPath()
    {
        // Arrange
        string json = Fixtures.ReadSarif("inspect-sample.json");

        // Act
        InspectIssue issue = SarifParser.Parse(json)[0];

        // Assert
        issue.File.ShouldNotStartWith("file://");
        issue.File.ShouldEndWith("HomeController.cs");
    }

    [Fact]
    public void Parse_NoteLevel_MapsToSuggestion()
    {
        // Arrange
        string json = Fixtures.ReadSarif("inspect-sample.json");

        // Act
        InspectIssue issue = SarifParser.Parse(json)[2];

        // Assert
        issue.Severity.ShouldBe("SUGGESTION");
        issue.File.ShouldEndWith("Invoice.cs");
    }

    [Fact]
    public void Parse_PlainPathUri_PassesThroughVerbatim()
    {
        // Arrange
        string json = Fixtures.ReadSarif("plain-path.json");

        // Act
        InspectIssue issue = SarifParser.Parse(json).ShouldHaveSingleItem();

        // Assert
        issue.File.ShouldBe("src/Models/Invoice.cs");
    }

    [Fact]
    public void Parse_ResultWithoutLocation_IsDropped()
    {
        // Arrange
        string json = Fixtures.ReadSarif("location-less.json");

        // Act
        InspectIssue issue = SarifParser.Parse(json).ShouldHaveSingleItem();

        // Assert
        issue.RuleId.ShouldBe("UnusedType.Global");
    }

    [Fact]
    public void Parse_MissingRegion_LineIsZeroAndEndLineNull()
    {
        // Arrange
        string json = Fixtures.ReadSarif("missing-region.json");

        // Act
        InspectIssue issue = SarifParser.Parse(json).ShouldHaveSingleItem();

        // Assert
        issue.Line.ShouldBe(0);
        issue.EndLine.ShouldBeNull();
    }

    [Fact]
    public void Parse_EmptyRuns_ReturnsNoIssues()
    {
        // Arrange
        string json = Fixtures.ReadSarif("empty-runs.json");

        // Act
        List<InspectIssue> issues = SarifParser.Parse(json);

        // Assert
        issues.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_NoRunsProperty_ReturnsNoIssues()
    {
        // Act
        List<InspectIssue> issues = SarifParser.Parse("{}");

        // Assert
        issues.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_MultipleRuns_FlattensAllResults()
    {
        // Arrange
        string json = Fixtures.ReadSarif("multiple-runs.json");

        // Act
        List<InspectIssue> issues = SarifParser.Parse(json);

        // Assert
        issues.Count.ShouldBe(2);
        issues.Select(i => i.RuleId).ShouldBe(["RedundantUsingDirective", "UnusedType.Global"]);
    }

    /// <summary>
    ///     A property found these; each is pinned here so the regression is nailed to a named input rather
    ///     than left to a generator redrawing it. <c>file://[zz]/</c> is the one it falsified on first — a
    ///     malformed IPv6 literal, which <see cref="Uri" /> rejects outright. A bare <c>file://</c> is
    ///     deliberately not among them: it parses, to a local path of <c>/</c>, so it is not this case at all.
    /// </summary>
    [Theory]
    [InlineData("file://[zz]/")]
    [InlineData("file://h^ost/p")]
    [InlineData("file://a|b")]
    public void Parse_MalformedFileUri_PassesItThroughVerbatim(string uri)
    {
        // Arrange
        var json = $$"""
                     {
                       "runs": [ { "results": [ {
                         "ruleId": "BadUri",
                         "level": "warning",
                         "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "{{uri}}" } } } ]
                       } ] } ]
                     }
                     """;

        // Act
        InspectIssue issue = SarifParser.Parse(json).ShouldHaveSingleItem();

        // Assert — the result survives, carrying the URI jb wrote. Anything else discards a whole report
        // over one unparseable path, after the caller has already paid for the run.
        issue.File.ShouldBe(uri);
        issue.RuleId.ShouldBe("BadUri");
    }

    [Theory]
    [InlineData("error", "ERROR")]
    [InlineData("warning", "WARNING")]
    [InlineData("note", "SUGGESTION")]
    [InlineData("hint", "HINT")]
    [InlineData("something-else", "SOMETHING-ELSE")]
    public void MapSeverity_Level_MapsToExpectedLabel(string level, string expected)
    {
        // Act
        string label = SarifParser.MapSeverity(level);

        // Assert
        label.ShouldBe(expected);
    }
}