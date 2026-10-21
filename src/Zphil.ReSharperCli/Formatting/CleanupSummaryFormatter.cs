using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     Renders a <see cref="CleanupOutcome" /> as a plain-text summary at a given <see cref="DetailLevel" />.
/// </summary>
/// <remarks>
///     The header is always present and states what this run measured (see <see cref="Header" />). Lower
///     levels collapse the lowest-signal categories to trailing counts.
/// </remarks>
internal static class CleanupSummaryFormatter
{
    /// <summary>
    ///     The sentence that closes a shrunken cleanup report, however it shrank, so an agent cannot read "less
    ///     was listed" as "less was done": the files are already rewritten.
    /// </summary>
    internal const string CleanupRanInFull = "The cleanup itself ran in full; only the report shrank.";

    // In the order their counts are emitted, lowest signal last: as trailing lines at the listing levels and as
    // the one parenthetical Minimal ends with. Collapsed decides which appear.
    private static readonly CollapsibleCategory[] Collapsible =
    [
        new(CleanupFileStatus.Unchanged, "unchanged", "unchanged"),
        new(CleanupFileStatus.StatusUnknown, "status unknown", "unknown"),
        new(CleanupFileStatus.Pattern, "pattern(s)", "pattern(s)")
    ];

    public static string Format(CleanupOutcome outcome, DetailLevel level)
    {
        Dictionary<CleanupFileStatus, int> counts = outcome.Entries.CountBy(entry => entry.Status).ToDictionary();
        int changed = counts.GetValueOrDefault(CleanupFileStatus.Changed);
        int unchanged = counts.GetValueOrDefault(CleanupFileStatus.Unchanged);
        int unknown = counts.GetValueOrDefault(CleanupFileStatus.StatusUnknown);
        int pattern = counts.GetValueOrDefault(CleanupFileStatus.Pattern);
        int hashed = changed + unchanged;

        string header = Header(outcome.Profile, changed, hashed, unknown, pattern);

        if (level == DetailLevel.Minimal)
        {
            List<string> phrases = Collapsed(counts, level)
                .Select(category => $"{counts[category.Status]} {category.MinimalNoun}")
                .ToList();
            if (phrases.Count == 0) return $"{header}.";

            string tail = string.Join(", ", phrases);
            return $"{header}. ({tail} not listed.)";
        }

        List<string> lines = [$"{header}:"];

        foreach (CleanupEntry entry in outcome.Entries)
            if (IsListed(entry.Status, level))
                lines.Add($"  - {entry.Display} ({StatusLabel(entry.Status)})");

        foreach (CollapsibleCategory category in Collapsed(counts, level))
            lines.Add($"  (+{counts[category.Status]} {category.Noun}, not listed)");

        return string.Join("\n", lines);
    }

    /// <summary>
    ///     Returns the categories <paramref name="level" /> counts rather than lists, in
    ///     <see cref="Collapsible" /> order.
    /// </summary>
    /// <remarks>
    ///     Minimal lists none, so at that level this is every non-zero category, and its one line follows the
    ///     same rule as the listing levels.
    /// </remarks>
    private static IEnumerable<CollapsibleCategory> Collapsed(
        IReadOnlyDictionary<CleanupFileStatus, int> counts, DetailLevel level)
    {
        return Collapsible.Where(category =>
            !IsListed(category.Status, level) && counts.GetValueOrDefault(category.Status) > 0);
    }

    /// <summary>
    ///     Returns the header body without its trailing punctuation (the listing levels append <c>:</c>, Minimal
    ///     a <c>.</c>), worded for what the entries let this run measure.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The count is a measurement over the files whose bytes were hashed both before and after the run,
    ///         and two kinds of entry fall outside it. A wildcard is never a single file: <c>jb</c> expands a
    ///         pattern against the solution model, so this server never learns which files it matched. A named
    ///         file that could not be read before or after the run was never compared, so counting it would
    ///         report a comparison nobody made.
    ///     </para>
    ///     <para>
    ///         With nothing hashed there is no ratio to state, and the header says why instead:
    ///         <c>0 of 0 file(s) changed on disk</c> would read as "nothing needed changing" for a run that may
    ///         have rewritten dozens of files. <see cref="CleanupRanInFull" /> cannot correct that reading,
    ///         because it closes only a reduced or truncated report, and an all-wildcard run is too short to be
    ///         either. When every named file was unreadable, that is the reason given whether or not wildcards
    ///         came with them, since every level lists or counts the wildcards.
    ///     </para>
    ///     <para>
    ///         The partial form adds the one word "hashed", which stays accurate whichever kind of entry was left
    ///         out.
    ///     </para>
    /// </remarks>
    private static string Header(string profile, int changed, int hashed, int unknown, int pattern)
    {
        var opening = $"Cleanup completed with profile \"{profile}\".";

        if (unknown == 0 && pattern == 0) return $"{opening} {changed} of {hashed} file(s) changed on disk";

        if (hashed > 0) return $"{opening} {changed} of {hashed} hashed file(s) changed on disk";

        if (unknown == 0)
            return $"{opening} Every entry was a wildcard pattern: jb cleaned what they matched, and this server "
                   + "hashes named files only, so it cannot report a count";

        return $"{opening} Every named file was unreadable before or after the run, so this server cannot say "
               + "whether jb changed them";
    }

    /// <summary>
    ///     Describes which categories stopped being listed individually at <paramref name="level" />, phrased
    ///     for the reduction note.
    /// </summary>
    /// <remarks>Must agree with <see cref="IsListed" />.</remarks>
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
    ///     Determines whether an entry of <paramref name="status" /> is listed individually at
    ///     <paramref name="level" /> rather than collapsed to a count.
    /// </summary>
    private static bool IsListed(CleanupFileStatus status, DetailLevel level)
    {
        return status switch
        {
            CleanupFileStatus.Changed => true, // Minimal returns before anything is listed
            CleanupFileStatus.StatusUnknown => level <= DetailLevel.Medium,
            CleanupFileStatus.Pattern => level <= DetailLevel.High,
            _ => level == DetailLevel.Full // Unchanged
        };
    }

    private static string StatusLabel(CleanupFileStatus status)
    {
        return status switch
        {
            CleanupFileStatus.Changed => "changed",
            CleanupFileStatus.Unchanged => "unchanged",
            CleanupFileStatus.StatusUnknown => "status unknown",
            _ => "pattern, not hashed"
        };
    }

    /// <summary>
    ///     Represents a status a level can count rather than list, with the noun its count takes on a listing
    ///     level's trailing line and the shorter one Minimal uses.
    /// </summary>
    private sealed record CollapsibleCategory(CleanupFileStatus Status, string Noun, string MinimalNoun);
}