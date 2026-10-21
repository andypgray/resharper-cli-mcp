using Zphil.ReSharperCli.Sarif;

namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     Renders inspection issues as a markdown summary at a given <see cref="DetailLevel" />.
/// </summary>
/// <remarks>
///     The header <c>Found {N} issue(s) across {M} file(s)</c> is invariant at every level, so a caller can
///     anchor on it whichever level fired, and every level counts every issue: a reduced result is complete
///     but less detailed, unlike a truncated one. Output uses <c>\n</c> line endings, never
///     <see cref="Environment.NewLine" />, and the formatter's own text is ASCII, so it is byte-for-byte
///     stable across platforms.
/// </remarks>
internal static class IssueMarkdownFormatter
{
    /// <summary>How to make the next scan return less.</summary>
    internal const string NarrowingHint = "Narrow the scan with the files parameter or raise severity.";

    /// <summary>How to get every finding out instead of asking for less.</summary>
    /// <remarks>
    ///     It costs no extra <c>jb</c> work: the issues are already parsed by the time the ladder runs.
    /// </remarks>
    internal const string FullReportHint =
        "Set report to Markdown to have every finding written to a file with its own message.";

    /// <summary>
    ///     Both remedies, in the order a caller should consider them, so any surface offering both spells each
    ///     as the reduction note does.
    /// </summary>
    internal const string TruncationRemedy = NarrowingHint + " " + FullReportHint;

    private const int MaxListedFiles = 8; // Medium: files still listed individually
    private const int MaxRolledUpRules = 15; // Low: rules in the "By rule" rollup
    private const int MaxRolledUpFiles = 10; // Low: files in the "By file" rollup
    private const int MaxMinimalRules = 3; // Minimal: rules named in the one-liner
    private const int MaxLineRanges = 12; // High/Medium: line ranges per collapsed group

    public static string Format(IReadOnlyList<InspectIssue> issues, DetailLevel level)
    {
        if (issues.Count == 0) return "No issues found.";

        List<FileGroup> files = GroupByFile(issues);

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
    ///     Describes what was given up at <paramref name="level" />, and what to do about it.
    /// </summary>
    /// <remarks>
    ///     <see cref="DetailLevel.High" /> carries no <see cref="NarrowingHint" />: re-running scoped repeats a
    ///     whole solution's analysis, and High fires on most solution-wide runs, so the hint there would be
    ///     noise. It does carry <see cref="FullReportHint" />, because the per-issue messages High collapses to
    ///     one example are what a report recovers.
    /// </remarks>
    /// <param name="level">The level the rendering settled at.</param>
    /// <param name="issueCount">
    ///     How many issues the rendering covers. At zero this returns <c>""</c>, which
    ///     <c>ProgressiveRenderer</c> reads as no reduction, so no note is emitted: <see cref="Format" />
    ///     renders <c>No issues found.</c> at every level, and a note would describe a listing that never
    ///     existed and offer a report of no findings.
    /// </param>
    /// <param name="reportWritten">
    ///     Whether this call already wrote a report. When it did, <see cref="FullReportHint" /> is omitted at
    ///     every level, because <see cref="InspectReportNote" /> has already named the file.
    /// </param>
    /// <param name="levelWasRequested">
    ///     Whether <paramref name="level" /> is the one the caller's <c>detail</c> asked for rather than one
    ///     the budget forced. When it is, <see cref="NarrowingHint" /> is omitted, since a caller that just
    ///     asked for less does not need telling to ask for less. Both flags are required, not optional, so a
    ///     call site cannot lose a suppression by forgetting one.
    /// </param>
    public static string DescribeReduction(
        DetailLevel level, int issueCount, bool reportWritten, bool levelWasRequested)
    {
        if (issueCount == 0) return "";

        string remedy = reportWritten ? "" : " " + FullReportHint;
        string narrowing = levelWasRequested ? "" : " " + NarrowingHint;

        return level switch
        {
            DetailLevel.High =>
                "issues repeating a rule within a file are collapsed to one line carrying their line numbers "
                + "and one example message. Every issue is still counted." + remedy,
            DetailLevel.Medium =>
                $"only the {MaxListedFiles} most-affected files are listed; the rest are counted."
                + narrowing + remedy,
            DetailLevel.Low =>
                "the per-file listing is replaced by a rollup of the top rules and the top files."
                + narrowing + remedy,
            _ => "totals, severity counts, and the top rules only." + narrowing + remedy
        };
    }

    /// <summary>
    ///     Renders the issues' start lines as an ascending, deduplicated, comma-separated list, collapsing
    ///     runs of two or more consecutive lines to <c>13-15</c>.
    /// </summary>
    /// <remarks>
    ///     Deduplication is deliberate: two issues can share a line, and the <c>x{n}</c> count carries the true
    ///     total while this list is only a locator. <see cref="InspectIssue.EndLine" /> goes unused, because a
    ///     range here spans several distinct issues rather than one issue's extent. Past
    ///     <see cref="MaxLineRanges" /> ranges the tail collapses to <c>(+N more)</c>.
    /// </remarks>
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

        return Collapse(ranges, MaxLineRanges);
    }

    /// <summary>
    ///     Joins <paramref name="items" /> with <c>", "</c>, listing at most <paramref name="maxListed" />
    ///     before the tail collapses to <c>(+N more)</c>.
    /// </summary>
    /// <remarks>
    ///     The one spelling of that collapse, so the shape an agent pattern-matches on cannot drift between the
    ///     surfaces that render it.
    /// </remarks>
    internal static string Collapse(IReadOnlyList<string> items, int maxListed)
    {
        if (items.Count <= maxListed) return string.Join(", ", items);

        return $"{string.Join(", ", items.Take(maxListed))} (+{items.Count - maxListed} more)";
    }

    /// <summary>Renders Full: every issue on its own line, grouped by file in first-seen order.</summary>
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

    /// <summary>
    ///     Renders High: every file listed, with issues repeating a rule within a file collapsed to one line.
    /// </summary>
    private static string FormatHigh(IReadOnlyList<InspectIssue> issues, List<FileGroup> files)
    {
        return FormatListing(issues, files, files.Count);
    }

    /// <summary>
    ///     Renders Medium: High's shape restricted to the most-affected files, the rest collapsed to a count.
    /// </summary>
    private static string FormatMedium(IReadOnlyList<InspectIssue> issues, List<FileGroup> files)
    {
        return FormatListing(issues, files, MaxListedFiles);
    }

    /// <summary>
    ///     Renders the shape High and Medium share: per-file sections whose repeated <c>(rule, severity)</c>
    ///     pairs are collapsed, listing at most <paramref name="maxFiles" /> files.
    /// </summary>
    private static string FormatListing(IReadOnlyList<InspectIssue> issues, List<FileGroup> files, int maxFiles)
    {
        // Selected by issue count but emitted in first-seen order. That ordering is what makes Medium
        // byte-identical to High whenever the file count is within the cap, so ProgressiveRenderer's
        // content-equality skip drops through the redundant level at no cost.
        HashSet<string> listed = files
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

    /// <summary>Renders Low: no per-issue detail, only the top rules and the top files, each with counts.</summary>
    private static string FormatRollup(IReadOnlyList<InspectIssue> issues, List<FileGroup> files)
    {
        List<RuleRollup> rules = RollUpRules(issues);

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

        IEnumerable<FileGroup> ranked = files.OrderByDescending(file => file.Issues.Count).Take(MaxRolledUpFiles);
        foreach (FileGroup file in ranked) lines.Add($"  {file.File}: {SeverityBreakdown(file.Issues)}");

        if (files.Count > MaxRolledUpFiles) lines.Add($"  (+{files.Count - MaxRolledUpFiles} file(s) not listed)");

        return string.Join("\n", lines);
    }

    /// <summary>Renders Minimal: one line of totals, severity counts, and the top rules.</summary>
    private static string FormatMinimal(IReadOnlyList<InspectIssue> issues, List<FileGroup> files)
    {
        List<RuleRollup> rules = RollUpRules(issues);

        string topRules = string.Join(
            ", ", rules.Take(MaxMinimalRules).Select(rule => $"`{rule.RuleId}` x{rule.Count}"));
        string remainder = rules.Count > MaxMinimalRules
            ? $" (+{rules.Count - MaxMinimalRules} rule(s) not listed)"
            : "";

        return $"{HeaderText(issues, files)}. {SeverityBreakdown(issues)}. Top rules: {topRules}{remainder}.";
    }

    /// <summary>
    ///     Returns the header body without its trailing punctuation: the listing levels append <c>:</c>, and
    ///     Minimal a <c>.</c>.
    /// </summary>
    private static string HeaderText(IReadOnlyList<InspectIssue> issues, List<FileGroup> files)
    {
        return $"Found {issues.Count} issue(s) across {files.Count} file(s)";
    }

    private static string IssueLine(InspectIssue issue)
    {
        return $"- **Line {issue.Line}** [{issue.Severity}] `{issue.RuleId}`: {issue.Message}";
    }

    /// <summary>Appends one rule group's lines.</summary>
    /// <remarks>
    ///     A singleton renders as it does at Full, so a file whose rules never repeat is byte-identical across
    ///     Full and High. A repeated group whose first message is empty gets no message line, because
    ///     <c>SarifParser</c> can yield an empty message.
    /// </remarks>
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
    ///     <see cref="Enumerable.GroupBy{TSource,TKey}(IEnumerable{TSource},Func{TSource,TKey})" /> guarantees.
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
    ///     Counts issues per <c>(rule, severity)</c> across the solution, keyed as the per-file collapse is, so
    ///     the severity shown beside a rule is always the one its issues were reported at.
    /// </summary>
    /// <remarks>
    ///     Ranked by count, then rule id. Severity-first would bury a 120-issue rule behind one stray ERROR, and
    ///     the per-file severity breakdown already makes errors unmissable. <c>OrderByDescending</c> is stable,
    ///     so a rule reported at two severities with equal counts keeps first-seen order.
    /// </remarks>
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

    /// <summary>Formats severity counts most-serious-first, for example <c>1 ERROR, 5 WARNING</c>.</summary>
    private static string SeverityBreakdown(IEnumerable<InspectIssue> issues)
    {
        IEnumerable<string> parts = issues
            .GroupBy(issue => issue.Severity, StringComparer.Ordinal)
            .OrderBy(group => SeverityRank(group.Key))
            .Select(group => $"{group.Count()} {group.Key}");

        return string.Join(", ", parts);
    }

    /// <summary>Ranks a severity label, most serious first.</summary>
    /// <remarks>
    ///     Any other label sorts last: <c>SarifParser.MapSeverity</c> passes an unrecognised jb level through
    ///     uppercased rather than dropping it, so an unknown label must still sort deterministically.
    /// </remarks>
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