using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Tests.Formatting;

public sealed class InspectReportNoteTests
{
    [Fact]
    public void For_NoReportAsked_IsEmpty()
    {
        // Arrange — the default path. An empty preamble is what keeps a response with no report
        // byte-for-byte what it was before this parameter existed.

        // Act
        string note = InspectReportNote.For(null, 3);

        // Assert
        note.ShouldBe("");
    }

    [Fact]
    public void For_AWrittenReport_NamesThePathAndTheCountAndEndsAsAPreamble()
    {
        // Arrange
        InspectReportOutcome outcome = new("/tmp/reports/App-inspect-abcd1234.md", null);

        // Act
        string note = InspectReportNote.For(outcome, 327);

        // Assert
        note.ShouldStartWith("FULL REPORT: all 327 issue(s)");
        note.ShouldContain("\"/tmp/reports/App-inspect-abcd1234.md\"");
        note.ShouldEndWith("\n\n"); // separated from the listing, like the other two preambles
    }

    [Fact]
    public void For_AWrittenReport_DoesNotClaimTheFileHoldsMoreThanTheResponse()
    {
        // Arrange — a scoped scan that fits at Full puts the same listing in both places, so a note
        // promising the file has what the response lacks would be false exactly there.
        InspectReportOutcome outcome = new("/tmp/reports/App-inspect-abcd1234.md", null);

        // Act
        string note = InspectReportNote.For(outcome, 2);

        // Assert
        note.ShouldContain("the same run rendered to fit the response budget");
    }

    [Fact]
    public void For_AFailedWrite_NamesTheFileAndTheReason()
    {
        // Arrange — the caller asked for this file by name and is not getting it, but the jb run behind the
        // summary already cost minutes, so the call reports rather than fails.
        InspectReportOutcome outcome = new("/tmp/reports/App-inspect-abcd1234.md", "Access to the path is denied.");

        // Act
        string note = InspectReportNote.For(outcome, 12);

        // Assert
        note.ShouldStartWith("WARNING: the full report could not be written");
        note.ShouldContain("\"/tmp/reports/App-inspect-abcd1234.md\"");
        note.ShouldContain("Access to the path is denied.");
        note.ShouldEndWith("\n\n");
    }

    [Fact]
    public void For_AFailedWriteWhoseReasonSpansLines_FlattensItOntoOne()
    {
        // Arrange — the reason is an exception message, and one carrying a newline would make the banner's
        // tail read as body text. Same contract ConfigWarningBanner states for its own reasons.
        InspectReportOutcome outcome = new("/tmp/reports/App-inspect-abcd1234.md", "Disk full.\nRetry later.");

        // Act
        string note = InspectReportNote.For(outcome, 1);

        // Assert
        note.TrimEnd('\n').ShouldNotContain("\n");
        note.ShouldContain("Disk full. Retry later.");
    }
}