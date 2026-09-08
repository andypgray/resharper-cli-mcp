using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     Renders a <see cref="CleanupOutcome" /> as a plain-text summary at a given <see cref="DetailLevel" />.
///     The header is always present and states what this run measured — see <see cref="Header" /> for the
///     three forms it takes, one per epistemic state the entry list left it in. Lower levels progressively
///     collapse the lowest-signal categories to trailing counts, so a solution-wide run degrades gracefully
///     instead of being hard-chopped.
///     A small batch fits at <see cref="DetailLevel.Full" />, whose output is a plain per-file list. Output uses
///     <c>\n</c> line endings and is ASCII-only, matching the other formatters.
/// </summary>
internal static class CleanupSummaryFormatter
{
    /// <summary>
    ///     Closes every reduction note, and the truncation footer too (via <c>ResharperTools.TruncationHintFor</c>).
    ///     A shrinking report is the one place an agent could read "less was listed" as "less was done", and the
    ///     whole point of this tool is that it already rewrote the files.
    /// </summary>
    internal const string CleanupRanInFull = "The cleanup itself ran in full; only the report shrank.";

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
        Dictionary<CleanupFileStatus, int> counts = outcome.Entries.CountBy(entry => entry.Status).ToDictionary();
        int changed = counts.GetValueOrDefault(CleanupFileStatus.Changed);
        int unchanged = counts.GetValueOrDefault(CleanupFileStatus.Unchanged);
        int unknown = counts.GetValueOrDefault(CleanupFileStatus.StatusUnknown);
        int pattern = counts.GetValueOrDefault(CleanupFileStatus.Pattern);
        int concrete = changed + unchanged + unknown;

        string header = Header(outcome.Profile, changed, concrete, pattern);

        if (level == DetailLevel.Minimal)
            return $"{header}. ({unchanged} unchanged, {unknown} unknown, {pattern} pattern(s) not listed.)";

        List<string> lines = [$"{header}:"];

        foreach (CleanupEntry entry in outcome.Entries)
            if (IsListed(entry.Status, level))
                lines.Add($"  - {entry.Display} ({StatusLabel(entry.Status)})");

        foreach (CleanupFileStatus status in CollapseOrder)
        {
            int count = counts.GetValueOrDefault(status);
            if (!IsListed(status, level) && count > 0) lines.Add($"  ({CollapsePhrase(status, count)})");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    ///     The header body, without its trailing punctuation — the listing levels append <c>:</c>, Minimal a
    ///     <c>.</c> — in one of three forms, one per epistemic state the entry list leaves this formatter in.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The count is a measurement, over exactly the entries whose bytes were hashed before and after the
    ///         run. A wildcard is outside it and always was: <c>jb</c> expands a pattern against the solution
    ///         model, so this server never learns which files it matched. What changes here is that the header
    ///         stops asserting a ratio over an empty knowledge set. <c>0 of 0 file(s) changed on disk</c> reads
    ///         as "nothing needed changing" for a run that may have rewritten dozens of files, which is the
    ///         reading <see cref="CleanupRanInFull" /> exists to prevent — and it cannot, because it rides only
    ///         in <see cref="DescribeReduction" />, and an all-wildcard run is a few lines that never reduce.
    ///     </para>
    ///     <para>
    ///         The partial form adds one word rather than a clause. Every level already carries the pattern
    ///         count — listed at Full and High, <c>(+N pattern(s), not listed)</c> below them, and in Minimal's
    ///         tail — so a header clause would state it twice in two vocabularies.
    ///     </para>
    /// </remarks>
    private static string Header(string profile, int changed, int concrete, int pattern)
    {
        var opening = $"Cleanup completed with profile \"{profile}\".";

        if (pattern == 0) return $"{opening} {changed} of {concrete} file(s) changed on disk";

        if (concrete > 0) return $"{opening} {changed} of {concrete} named file(s) changed on disk";

        return $"{opening} Every entry was a wildcard pattern: jb cleaned what they matched, and this server "
               + "hashes named files only, so it cannot report a count";
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
}