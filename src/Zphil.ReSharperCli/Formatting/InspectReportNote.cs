using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     The line an inspect result leads with when the caller asked for a report file: where it went, or why
///     it did not. The last of inspect's preambles, so it sits immediately above the listing it refers to.
/// </summary>
/// <remarks>
///     <para>
///         Like its neighbours it is charged to the budget by <c>ResponseTruncator.BudgetForBody</c>
///         before rendering, which is what puts it outside the reduction ladder — a note naming the file
///         would otherwise vanish at <c>Minimal</c>, precisely when the response is most reduced and the file
///         matters most. Being a prefix, it survives hard truncation too.
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
    ///     The note for <paramref name="outcome" />, or <c>""</c> when no report was asked for
    ///     (<paramref name="outcome" /> is <see langword="null" />) — in which case the response is
    ///     byte-for-byte what it has always been.
    /// </summary>
    public static string For(InspectReportOutcome? outcome, int issueCount)
    {
        if (outcome is null) return "";

        if (outcome.Failure is { } failure)
            return $"WARNING: the full report could not be written to \"{outcome.Path}\" "
                   + $"({ConfigWarningBanner.SingleLine(failure)}). Only the listing below came back from this "
                   + "call, reduced if it did not fit the response budget.\n\n";

        // A run that found nothing has no listing for the file to hold more of, so the budget clause would
        // be describing a reduction of nothing. The file is still written and still named: a report was asked
        // for, so the response names a file that exists, and a dated clean bill of health is what it carries.
        if (issueCount == 0)
            return $"FULL REPORT: no issues found; the run and its scope were written to \"{outcome.Path}\".\n\n";

        return $"FULL REPORT: all {issueCount} issue(s), each with its own message, written to "
               + $"\"{outcome.Path}\". The listing below is the same run rendered to fit the response "
               + "budget.\n\n";
    }
}