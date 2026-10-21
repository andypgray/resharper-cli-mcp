using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     Renders the line an inspect result leads with when the caller asked for a report file: where it went,
///     or why it did not.
/// </summary>
/// <remarks>
///     <para>
///         As a prefix outside the reduction ladder it survives <c>Minimal</c> and hard truncation alike, which
///         is when the response is most reduced and the file matters most.
///     </para>
///     <para>
///         It does not claim the file holds anything the response lacks. A scoped scan that fits at
///         <see cref="DetailLevel.Full" /> puts the same listing in both places, and a note promising
///         otherwise would be wrong there.
///     </para>
/// </remarks>
internal static class InspectReportNote
{
    /// <summary>
    ///     Renders the note for <paramref name="outcome" />, or <c>""</c> when no report was asked for
    ///     (<paramref name="outcome" /> is <see langword="null" />).
    /// </summary>
    public static string For(InspectReportOutcome? outcome, int issueCount)
    {
        if (outcome is null) return "";

        if (outcome.Failure is { } failure)
            return $"WARNING: the full report could not be written to \"{outcome.Path}\" "
                   + $"({ConfigWarningBanner.SingleLine(failure)}). Only the listing below came back from this "
                   + "call, reduced if it did not fit the response budget.\n\n";

        // A run that found nothing has no listing for the file to hold more of, so the budget clause would
        // describe a reduction of nothing. The file is still written and named, because a report was asked for.
        if (issueCount == 0)
            return $"FULL REPORT: no issues found; the run and its scope were written to \"{outcome.Path}\".\n\n";

        return $"FULL REPORT: all {issueCount} issue(s), each with its own message, written to "
               + $"\"{outcome.Path}\". The listing below is the same run rendered to fit the response "
               + "budget.\n\n";
    }
}