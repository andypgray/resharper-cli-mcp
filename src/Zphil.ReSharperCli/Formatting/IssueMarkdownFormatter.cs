using Zphil.ReSharperCli.Sarif;

namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     Renders inspection issues as a markdown summary grouped by file, in first-seen file order.
///     Output uses <c>\n</c> line endings exclusively (never <see cref="Environment.NewLine" />) so it is
///     byte-for-byte stable across platforms.
/// </summary>
internal static class IssueMarkdownFormatter
{
    public static string Format(IReadOnlyList<InspectIssue> issues)
    {
        if (issues.Count == 0) return "No issues found.";

        // Group by file, preserving first-seen order (Dictionary enumeration order is not guaranteed).
        Dictionary<string, List<InspectIssue>> grouped = new(StringComparer.Ordinal);
        List<string> fileOrder = [];
        foreach (InspectIssue issue in issues)
        {
            if (!grouped.TryGetValue(issue.File, out var fileIssues))
            {
                fileIssues = [];
                grouped[issue.File] = fileIssues;
                fileOrder.Add(issue.File);
            }

            fileIssues.Add(issue);
        }

        List<string> lines =
        [
            $"Found {issues.Count} issue(s) across {fileOrder.Count} file(s):",
            ""
        ];

        foreach (string file in fileOrder)
        {
            lines.Add($"### {file}");
            foreach (InspectIssue issue in grouped[file]) lines.Add($"- **Line {issue.Line}** [{issue.Severity}] `{issue.RuleId}`: {issue.Message}");

            lines.Add("");
        }

        return string.Join("\n", lines);
    }
}