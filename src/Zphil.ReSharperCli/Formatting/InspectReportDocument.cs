using System.Globalization;

namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     Composes the file <c>resharper_inspect</c> writes when a caller asks for a report: a short provenance
///     header, then the <see cref="IssueMarkdownFormatter" /> rendering at <see cref="DetailLevel.Full" />.
/// </summary>
/// <remarks>
///     <para>
///         The header lives here rather than in <see cref="IssueMarkdownFormatter" />, whose output is pinned at
///         every level and must render byte-for-byte without it. A report is read later, often by another agent,
///         and <c>Found N issue(s) across M file(s)</c> alone does not say which solution, at which severity, or
///         over which scope.
///     </para>
///     <para>
///         The timestamp is passed in, so this stays a pure function of its arguments. It is written in the
///         invariant culture, because the current one would give a Buddhist-calendar year under th-TH and a
///         <c>.</c> time separator under da-DK. Output uses <c>\n</c> line endings only.
///     </para>
/// </remarks>
internal static class InspectReportDocument
{
    public static string Compose(
        string fullListing,
        string solutionPath,
        string severity,
        IReadOnlyList<string>? scope,
        DateTimeOffset generatedAt)
    {
        string[] header =
        [
            "# ReSharper inspection report",
            "",
            $"- Solution: {solutionPath}",
            $"- Minimum severity: {severity}",
            $"- Scope: {DescribeScope(scope)}",
            $"- Generated: {generatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC",
            "",
            ""
        ];

        return string.Join("\n", header) + fullListing;
    }

    /// <summary>
    ///     Describes what the run was narrowed to, spelled as the caller gave it.
    /// </summary>
    /// <remarks>
    ///     A scan of everything says so rather than leaving the line blank, because "no scope" and "scope not
    ///     recorded" read the same on an empty value.
    /// </remarks>
    private static string DescribeScope(IReadOnlyList<string>? scope)
    {
        return scope is { Count: > 0 } ? string.Join(", ", scope) : "whole solution";
    }
}