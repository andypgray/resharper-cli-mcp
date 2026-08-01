using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     Renders a <see cref="CleanupOutcome" /> as a plain-text summary at a given <see cref="DetailLevel" />.
///     The header is always present and states <c>{changed} of {concrete}</c> files changed on disk, where
///     <c>{concrete}</c> counts non-pattern entries (a <see cref="CleanupFileStatus.StatusUnknown" /> file is
///     in the denominator but never the numerator). Lower levels progressively collapse the lowest-signal
///     categories to trailing counts, so a solution-wide run degrades gracefully instead of being hard-chopped.
///     A small batch fits at <see cref="DetailLevel.Full" />, whose output is a plain per-file list. Output uses
///     <c>\n</c> line endings and is ASCII-only, matching the other formatters.
/// </summary>
internal static class CleanupSummaryFormatter
{
    // Closes every reduction note. A shrinking report is the one place an agent could read "less was
    // listed" as "less was done", and the whole point of this tool is that it already rewrote the files.
    private const string CleanupRanInFull = "The cleanup itself ran in full; only the report shrank.";

    // The order trailing collapsed counts are emitted in (lowest signal last). A category appears here only
    // when this level does not list it individually and its count is non-zero.
    private static readonly CleanupFileStatus[] CollapseOrder =
    [
        CleanupFileStatus.Unchanged,
        CleanupFileStatus.StatusUnknown,
        CleanupFileStatus.Pattern
    ];

    public static string Format(CleanupOutcome outcome, DetailLevel level)
    {
        int changed = Count(outcome, CleanupFileStatus.Changed);
        int unchanged = Count(outcome, CleanupFileStatus.Unchanged);
        int unknown = Count(outcome, CleanupFileStatus.StatusUnknown);
        int pattern = Count(outcome, CleanupFileStatus.Pattern);
        int concrete = changed + unchanged + unknown;

        if (level == DetailLevel.Minimal)
            return $"Cleanup completed with profile \"{outcome.Profile}\". {changed} of {concrete} file(s) changed on disk. "
                   + $"({unchanged} unchanged, {unknown} unknown, {pattern} pattern(s) not listed.)";

        List<string> lines =
        [
            $"Cleanup completed with profile \"{outcome.Profile}\". {changed} of {concrete} file(s) changed on disk:"
        ];

        foreach (CleanupEntry entry in outcome.Entries)
            if (IsListed(entry.Status, level))
                lines.Add($"  - {entry.Display} ({StatusLabel(entry.Status)})");

        foreach (CleanupFileStatus status in CollapseOrder)
        {
            int count = Count(outcome, status);
            if (!IsListed(status, level) && count > 0) lines.Add($"  ({CollapsePhrase(status, count)})");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    ///     Which categories stopped being listed individually at <paramref name="level" />, for
    ///     <c>ProgressiveRenderer</c>'s reduction note. Mirrors <see cref="IsListed" /> — keep the two in step.
    /// </summary>
    public static string DescribeReduction(DetailLevel level)
    {
        return level switch
        {
            DetailLevel.High => $"unchanged files are counted rather than listed. {CleanupRanInFull}",
            DetailLevel.Medium =>
                $"unchanged files and wildcard patterns are counted rather than listed. {CleanupRanInFull}",
            DetailLevel.Low =>
                $"only the files cleanup changed are listed; every other category is counted. {CleanupRanInFull}",
            _ => $"counts only, with no per-file listing. {CleanupRanInFull}"
        };
    }

    /// <summary>
    ///     Whether an entry of <paramref name="status" /> is listed individually at <paramref name="level" /> (else
    ///     collapsed to a count).
    /// </summary>
    private static bool IsListed(CleanupFileStatus status, DetailLevel level)
    {
        return status switch
        {
            CleanupFileStatus.Changed => true, // always listed at every listing level (Minimal returns earlier)
            CleanupFileStatus.StatusUnknown => level <= DetailLevel.Medium, // Full, High, Medium
            CleanupFileStatus.Pattern => level <= DetailLevel.High, // Full, High
            _ => level == DetailLevel.Full // Unchanged: only at Full
        };
    }

    private static string StatusLabel(CleanupFileStatus status)
    {
        return status switch
        {
            CleanupFileStatus.Changed => "changed",
            CleanupFileStatus.Unchanged => "unchanged",
            CleanupFileStatus.StatusUnknown => "status unknown",
            _ => "pattern, not tracked"
        };
    }

    private static string CollapsePhrase(CleanupFileStatus status, int count)
    {
        return status switch
        {
            CleanupFileStatus.Unchanged => $"+{count} unchanged, not listed",
            CleanupFileStatus.StatusUnknown => $"+{count} status unknown, not listed",
            _ => $"+{count} pattern(s), not listed"
        };
    }

    private static int Count(CleanupOutcome outcome, CleanupFileStatus status)
    {
        var count = 0;
        foreach (CleanupEntry entry in outcome.Entries)
            if (entry.Status == status)
                count++;

        return count;
    }
}