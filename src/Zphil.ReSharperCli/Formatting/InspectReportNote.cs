using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     The line an inspect result leads with when the caller asked for a report file: where it went, or why
///     it did not. The third preamble beside <see cref="ConfigWarningBanner" /> and
///     <see cref="CompilationErrorNote" />, and last of the three, so it sits immediately above the listing
///     it refers to.
/// </summary>
/// <remarks>
///     <para>
///         Like its two neighbours it is charged to the budget by <c>ResponseTruncator.BudgetForBody</c>
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

        return $"FULL REPORT: all {issueCount} issue(s), each with its own message, written to "
               + $"\"{outcome.Path}\". The listing below is the same run rendered to fit the response "
               + "budget.\n\n";
    }
}