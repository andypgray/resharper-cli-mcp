using Zphil.ReSharperCli.Sarif;

namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     Renders inspection issues as a markdown summary at a given <see cref="DetailLevel" />. The header
///     <c>Found {N} issue(s) across {M} file(s)</c> is invariant at every level; below it, lower levels
///     progressively collapse the listing so a solution-wide run degrades gracefully instead of being
///     hard-chopped: <see cref="DetailLevel.Full" /> lists every issue, <see cref="DetailLevel.High" />
///     collapses issues repeating a rule within a file to one line carrying their line numbers,
///     <see cref="DetailLevel.Medium" /> narrows that to the most-affected files,
///     <see cref="DetailLevel.Low" /> replaces the per-file listing with a rules-and-files rollup, and
///     <see cref="DetailLevel.Minimal" /> is the one-liner. Every level counts every issue — a reduced
///     result is complete but less detailed, unlike a truncated one. Output uses <c>\n</c> line endings
///     exclusively (never <see cref="Environment.NewLine" />) and is ASCII-only, so it is byte-for-byte
///     stable across platforms.
/// </summary>
internal static class IssueMarkdownFormatter
{
    /// <summary>
    ///     How to make the next scan return less. Shared with <c>ResponseTruncator</c>'s truncation footer so
    ///     an agent meets one remedy rather than two spellings of it.
    /// </summary>
    internal const string NarrowingHint = "Narrow the scan with the files parameter or raise severity.";

    private const int MaxListedFiles = 8; // Medium: files still listed individually
    private const int MaxRolledUpRules = 15; // Low: rules in the "By rule" rollup
    private const int MaxRolledUpFiles = 10; // Low: files in the "By file" rollup
    private const int MaxMinimalRules = 3; // Minimal: rules named in the one-liner
    private const int MaxLineRanges = 12; // High/Medium: line ranges per collapsed group

    public static string Format(IReadOnlyList<InspectIssue> issues, DetailLevel level)
    {
        if (issues.Count == 0) return "No issues found.";

        var files = GroupByFile(issues);

        return level switch
        {
            DetailLevel.Full => FormatFull(issues, files),
            DetailLevel.High => FormatHigh(issues, files),
            DetailLevel.Medium => FormatMedium(issues, files),
            DetailLevel.Low => FormatRollup(issues, files),
            _ => FormatMinimal(issues, files)
        };
    }

    /// <summary>
    ///     What was given up at <paramref name="level" />, for <c>ProgressiveRenderer</c>'s reduction note.
    ///     <see cref="DetailLevel.High" /> deliberately carries no <see cref="NarrowingHint" />: nothing became
    ///     unreachable there, so telling an agent to re-run scoped would be noise on the level that fires for
    ///     essentially every solution-wide run.
    /// </summary>
    public static string DescribeReduction(DetailLevel level)
    {
        return level switch
        {
            DetailLevel.High =>
                "issues repeating a rule within a file are collapsed to one line carrying their line numbers "
                + "and one example message. Every issue is still counted.",
            DetailLevel.Medium =>
                $"only the {MaxListedFiles} most-affected files are listed; the rest are counted. {NarrowingHint}",
            DetailLevel.Low =>
                $"the per-file listing is replaced by a rollup of the top rules and the top files. {NarrowingHint}",
            _ => $"totals, severity counts, and the top rules only. {NarrowingHint}"
        };
    }

    /// <summary>
    ///     Renders issue <b>start</b> lines as an ascending, deduplicated, comma-separated list, collapsing
    ///     runs of two or more consecutive lines to <c>13-15</c>. Deduplication is deliberate: two issues can
    ///     share a line, and the <c>x{n}</c> count carries the true total while this list is only a locator.
    ///     <see cref="InspectIssue.EndLine" /> is intentionally unused — a range here spans several distinct
    ///     issues, not one issue's extent. Past <see cref="MaxLineRanges" /> ranges the tail collapses to
    ///     <c>(+N more)</c>.
    /// </summary>
    internal static string FormatLineRanges(IEnumerable<int> lines)
    {
        int[] ordered = lines.Distinct().Order().ToArray();

        List<string> ranges = [];
        for (var start = 0; start < ordered.Length;)
        {
            int end = start;
            while (end + 1 < ordered.Length && ordered[end + 1] == ordered[end] + 1) end++;

            ranges.Add(end > start ? $"{ordered[start]}-{ordered[end]}" : ordered[start].ToString());
            start = end + 1;
        }

        if (ranges.Count <= MaxLineRanges) return string.Join(", ", ranges);

        return $"{string.Join(", ", ranges.Take(MaxLineRanges))} (+{ranges.Count - MaxLineRanges} more)";
    }

    /// <summary>Full: every issue on its own line, grouped by file in first-seen order.</summary>
    private static string FormatFull(IReadOnlyList<InspectIssue> issues, List<FileGroup> files)
    {
        List<string> lines = [$"{HeaderText(issues, files)}:", ""];

        foreach (FileGroup file in files)
        {
            lines.Add($"### {file.File}");
            foreach (InspectIssue issue in file.Issues) lines.Add(IssueLine(issue));

            lines.Add("");
        }

        return string.Join("\n", lines);
    }

    /// <summary>High: every file listed, with issues repeating a rule within a file collapsed to one line.</summary>
    private static string FormatHigh(IReadOnlyList<InspectIssue> issues, List<FileGroup> files)
    {
        return FormatListing(issues, files, files.Count);
    }

    /// <summary>Medium: High's shape restricted to the most-affected files, the rest collapsed to a count.</summary>
    private static string FormatMedium(IReadOnlyList<InspectIssue> issues, List<FileGroup> files)
    {
        return FormatListing(issues, files, MaxListedFiles);
    }

    /// <summary>
    ///     The shared High/Medium shape: per-file sections whose repeated <c>(rule, severity)</c> pairs are
    ///     collapsed, listing at most <paramref name="maxFiles" /> files.
    /// </summary>
    private static string FormatListing(IReadOnlyList<InspectIssue> issues, List<FileGroup> files, int maxFiles)
    {
        // Selected by issue count but emitted in first-seen order. That ordering is what makes Medium
        // byte-identical to High whenever the file count is within the cap, so ProgressiveRenderer's
        // content-equality skip drops through the redundant level at no cost.
        var listed = files
            .OrderByDescending(file => file.Issues.Count)
            .Take(maxFiles)
            .Select(file => file.File)
            .ToHashSet(StringComparer.Ordinal);

        List<string> lines = [$"{HeaderText(issues, files)}:", ""];

        var omittedFiles = 0;
        var omittedIssues = 0;
        foreach (FileGroup file in files)
        {
            if (!listed.Contains(file.File))
            {
                omittedFiles++;
                omittedIssues += file.Issues.Count;
                continue;
            }

            lines.Add($"### {file.File}");
            foreach (RuleGroup rule in GroupByRule(file.Issues)) AppendRuleGroup(lines, rule);

            lines.Add("");
        }

        if (omittedFiles > 0) lines.Add($"  (+{omittedFiles} file(s) with {omittedIssues} issue(s), not listed)");

        return string.Join("\n", lines);
    }

    /// <summary>Low: no per-issue detail at all — the top rules and the top files, each with counts.</summary>
    private static string FormatRollup(IReadOnlyList<InspectIssue> issues, List<FileGroup> files)
    {
        var rules = RollUpRules(issues);

        List<string> lines =
        [
            $"{HeaderText(issues, files)}:",
            "",
            $"By rule ({Math.Min(MaxRolledUpRules, rules.Count)} of {rules.Count}):"
        ];

        foreach (RuleRollup rule in rules.Take(MaxRolledUpRules))
            lines.Add($"  `{rule.RuleId}` [{rule.Severity}]: {rule.Count} issue(s) in {rule.FileCount} file(s)");

        if (rules.Count > MaxRolledUpRules) lines.Add($"  (+{rules.Count - MaxRolledUpRules} rule(s) not listed)");

        lines.Add("");
        lines.Add($"By file ({Math.Min(MaxRolledUpFiles, files.Count)} of {files.Count}):");

        var ranked = files.OrderByDescending(file => file.Issues.Count).Take(MaxRolledUpFiles);
        foreach (FileGroup file in ranked) lines.Add($"  {file.File}: {SeverityBreakdown(file.Issues)}");

        if (files.Count > MaxRolledUpFiles) lines.Add($"  (+{files.Count - MaxRolledUpFiles} file(s) not listed)");

        return string.Join("\n", lines);
    }

    /// <summary>Minimal: one line of totals, severity counts, and the top rules.</summary>
    private static string FormatMinimal(IReadOnlyList<InspectIssue> issues, List<FileGroup> files)
    {
        var rules = RollUpRules(issues);

        string topRules = string.Join(
            ", ", rules.Take(MaxMinimalRules).Select(rule => $"`{rule.RuleId}` x{rule.Count}"));
        string remainder = rules.Count > MaxMinimalRules
            ? $" (+{rules.Count - MaxMinimalRules} rule(s) not listed)"
            : "";

        return $"{HeaderText(issues, files)}. {SeverityBreakdown(issues)}. Top rules: {topRules}{remainder}.";
    }

    /// <summary>
    ///     The header body, without its trailing punctuation. Invariant at every level (the listing levels
    ///     append <c>:</c>, Minimal a <c>.</c>), so a caller can anchor on it whichever level fired.
    /// </summary>
    private static string HeaderText(IReadOnlyList<InspectIssue> issues, List<FileGroup> files)
    {
        return $"Found {issues.Count} issue(s) across {files.Count} file(s)";
    }

    private static string IssueLine(InspectIssue issue)
    {
        return $"- **Line {issue.Line}** [{issue.Severity}] `{issue.RuleId}`: {issue.Message}";
    }

    /// <summary>
    ///     Emits one rule group: a singleton renders exactly as it does at Full, so a file whose rules never
    ///     repeat is byte-identical across Full and High. A repeated group becomes a count, a line-range list,
    ///     and one message — the shared text when every issue in the group says the same thing (lossless), the
    ///     first prefixed with <c>e.g. </c> when they differ, and nothing at all when it is empty
    ///     (<c>SarifParser</c> can yield an empty message).
    /// </summary>
    private static void AppendRuleGroup(List<string> lines, RuleGroup rule)
    {
        if (rule.Issues.Count == 1)
        {
            lines.Add(IssueLine(rule.Issues[0]));
            return;
        }

        string ranges = FormatLineRanges(rule.Issues.Select(issue => issue.Line));
        lines.Add($"- **`{rule.RuleId}`** [{rule.Severity}] x{rule.Issues.Count}, lines {ranges}");

        string first = rule.Issues[0].Message;
        if (first.Length == 0) return;

        bool identical = rule.Issues.All(issue => string.Equals(issue.Message, first, StringComparison.Ordinal));
        lines.Add(identical ? $"  - {first}" : $"  - e.g. {first}");
    }

    /// <summary>
    ///     Groups by file in first-seen order, preserving source order within each file — what
    ///     <see cref="Enumerable.GroupBy{TSource,TKey}(IEnumerable{TSource},Func{TSource,TKey})" /> guarantees,
    ///     and what Full has always emitted.
    /// </summary>
    private static List<FileGroup> GroupByFile(IReadOnlyList<InspectIssue> issues)
    {
        return issues
            .GroupBy(issue => issue.File, StringComparer.Ordinal)
            .Select(group => new FileGroup(group.Key, [.. group]))
            .ToList();
    }

    /// <summary>Groups one file's issues by <c>(rule, severity)</c>, in first-occurrence order.</summary>
    private static List<RuleGroup> GroupByRule(List<InspectIssue> issues)
    {
        return issues
            .GroupBy(issue => (issue.RuleId, issue.Severity))
            .Select(group => new RuleGroup(group.Key.RuleId, group.Key.Severity, [.. group]))
            .ToList();
    }

    /// <summary>
    ///     Solution-wide rule counts, keyed by <c>(rule, severity)</c> exactly as the per-file collapse is, so
    ///     the severity shown beside a rule is always the one its issues were reported at. Ranked by count
    ///     alone — severity-first would bury a 120-issue rule behind one stray ERROR, and the per-file severity
    ///     breakdown already makes errors unmissable. <c>OrderByDescending</c> is stable, so equal counts keep
    ///     first-seen order before the rule-id tie-break applies.
    /// </summary>
    private static List<RuleRollup> RollUpRules(IReadOnlyList<InspectIssue> issues)
    {
        return issues
            .GroupBy(issue => (issue.RuleId, issue.Severity))
            .Select(group => new RuleRollup(
                group.Key.RuleId,
                group.Key.Severity,
                group.Count(),
                group.Select(issue => issue.File).Distinct(StringComparer.Ordinal).Count()))
            .OrderByDescending(rule => rule.Count)
            .ThenBy(rule => rule.RuleId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Severity counts most-serious-first, for example <c>1 ERROR, 5 WARNING</c>.</summary>
    private static string SeverityBreakdown(IEnumerable<InspectIssue> issues)
    {
        var parts = issues
            .GroupBy(issue => issue.Severity, StringComparer.Ordinal)
            .OrderBy(group => SeverityRank(group.Key))
            .Select(group => $"{group.Count()} {group.Key}");

        return string.Join(", ", parts);
    }

    /// <summary>
    ///     Orders a severity label most-serious-first. The trailing bucket is load-bearing:
    ///     <c>SarifParser.MapSeverity</c> passes an unrecognised jb level through uppercased rather than
    ///     dropping it, so an unknown label must still sort deterministically.
    /// </summary>
    private static int SeverityRank(string severity)
    {
        return severity switch
        {
            "ERROR" => 0,
            "WARNING" => 1,
            "SUGGESTION" => 2,
            "HINT" => 3,
            _ => 4
        };
    }

    private sealed record FileGroup(string File, List<InspectIssue> Issues);

    private sealed record RuleGroup(string RuleId, string Severity, List<InspectIssue> Issues);

    private sealed record RuleRollup(string RuleId, string Severity, int Count, int FileCount);
}