using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Sarif;

namespace Zphil.ReSharperCli.Tests.Formatting;

public sealed class InspectReportDocumentTests
{
    private static readonly DateTimeOffset GeneratedAt = new(2026, 8, 24, 9, 30, 15, TimeSpan.Zero);

    private static readonly IReadOnlyList<InspectIssue> Issues =
    [
        new("/repo/src/A.cs", 3, null, "WARNING", "UnusedMember.Global", "Method is never used"),
        new("/repo/src/A.cs", 9, null, "WARNING", "UnusedMember.Global", "Property is never used")
    ];

    /// <summary>The body as the tool renders it once and shares between the response and the report.</summary>
    private static readonly string FullListing = IssueMarkdownFormatter.Format(Issues, DetailLevel.Full);

    [Fact]
    public void Compose_CarriesTheProvenanceARunCannotBeReconstructedWithout()
    {
        // Arrange — a report is read minutes or days later, often by an agent that did not make the call, and
        // the formatter's own header says only how many issues there were.

        // Act
        string document = InspectReportDocument.Compose(
            FullListing, "/repo/App.slnx", "SUGGESTION", ["src/**/*.cs"], GeneratedAt);

        // Assert
        document.ShouldStartWith("# ReSharper inspection report\n");
        document.ShouldContain("- Solution: /repo/App.slnx");
        document.ShouldContain("- Minimum severity: SUGGESTION");
        document.ShouldContain("- Scope: src/**/*.cs");
        document.ShouldContain("- Generated: 2026-08-24 09:30:15 UTC");
    }

    [Fact]
    public void Compose_AnUnscopedRun_SaysSoRatherThanLeavingTheLineBlank()
    {
        // Arrange — "no scope" and "scope I failed to record" read identically on an empty value. Both ways
        // a run can be unscoped reach here: no files argument at all, and one that split to nothing.
        IReadOnlyList<string>[] unscoped = [null!, []];

        // Act / Assert
        foreach (IReadOnlyList<string> scope in unscoped)
            InspectReportDocument.Compose(FullListing, "/repo/App.slnx", "WARNING", scope, GeneratedAt)
                .ShouldContain("- Scope: whole solution");
    }

    [Fact]
    public void Compose_AppendsTheFullListingUnmodified()
    {
        // Arrange — the whole point: at High, which is where a solution-wide response lands, these two would
        // collapse to one line carrying one example message. The Full listing keeps both, byte for byte.

        // Act
        string document = InspectReportDocument.Compose(FullListing, "/repo/App.slnx", "WARNING", null, GeneratedAt);

        // Assert
        document.ShouldContain("Method is never used");
        document.ShouldContain("Property is never used");
        document.ShouldEndWith(FullListing);
    }

    [Fact]
    public void Compose_UsesNewlineOnlyLineEndings()
    {
        // Arrange — matching the formatter it wraps, so the artifact is byte-stable across platforms.

        // Act
        string document = InspectReportDocument.Compose(FullListing, "/repo/App.slnx", "WARNING", null, GeneratedAt);

        // Assert
        document.ShouldNotContain("\r");
    }
}