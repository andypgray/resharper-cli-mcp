using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Tests.Formatting;

public sealed class InspectReportNoteTests
{
    private const string ReportPath = "/tmp/reports/App-inspect-abcd1234.md";

    [Fact]
    public void For_NoReportAsked_IsEmpty()
    {
        // Arrange — the default path. An empty preamble leaves a response that asked for no report exactly
        // as it would read without this parameter.

        // Act
        string note = InspectReportNote.For(null, 3);

        // Assert
        note.ShouldBe("");
    }

    [Fact]
    public void For_AWrittenReport_NamesTheFileAndSaysTheListingIsTheSameRun()
    {
        // Arrange
        InspectReportOutcome outcome = new(ReportPath, null);

        // Act
        string note = InspectReportNote.For(outcome, 327);

        // Assert — "the same run", never "more than the response": a scoped scan that fits at Full puts the
        // same listing in both places, so a note promising the file has what the response lacks would be
        // false exactly there. The blank line separates it from the listing, like the other preambles.
        note.ShouldBe(
            $"FULL REPORT: all 327 issue(s), each with its own message, written to \"{ReportPath}\". "
            + "The listing below is the same run rendered to fit the response budget.\n\n");
    }

    [Fact]
    public void For_AWrittenReportWithNoIssues_NamesTheFileWithoutTheBudgetClaim()
    {
        // Arrange — the file is still written, because "a report was asked for, so the response names a file
        // that exists" is a contract a caller can script against. What it must not say is that the listing
        // below was rendered to fit a budget: there is no listing, and nothing was reduced.
        InspectReportOutcome outcome = new(ReportPath, null);

        // Act
        string note = InspectReportNote.For(outcome, 0);

        // Assert — what the file does hold is the provenance header: solution, severity, scope, timestamp.
        // A dated clean bill of health, which is worth naming.
        note.ShouldBe(
            "FULL REPORT: no issues found; the run and its scope were written to "
            + $"\"{ReportPath}\".\n\n");
        note.ShouldNotContain("all 0 issue(s)");
        note.ShouldNotContain("rendered to fit the response budget");
    }

    [Fact]
    public void For_AFailedWrite_NamesTheFileAndTheReason()
    {
        // Arrange — the caller asked for this file by name and is not getting it, but the jb run behind the
        // summary already cost minutes, so the call reports rather than fails.
        InspectReportOutcome outcome = new(ReportPath, "Access to the path is denied.");

        // Act
        string note = InspectReportNote.For(outcome, 12);

        // Assert
        note.ShouldStartWith("WARNING: the full report could not be written");
        note.ShouldContain($"\"{ReportPath}\"");
        note.ShouldContain("Access to the path is denied.");
        note.ShouldEndWith("\n\n");
    }

    [Fact]
    public void For_AFailedWriteWhoseReasonSpansLines_FlattensItOntoOne()
    {
        // Arrange — the reason is an exception message, and one carrying a newline would make the banner's
        // tail read as body text. Same contract ConfigWarningBanner states for its own reasons.
        InspectReportOutcome outcome = new(ReportPath, "Disk full.\nRetry later.");

        // Act
        string note = InspectReportNote.For(outcome, 1);

        // Assert
        note.TrimEnd('\n').ShouldNotContain("\n");
        note.ShouldContain("Disk full. Retry later.");
    }
}