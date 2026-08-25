namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     The body of the file <c>resharper_inspect</c> writes when a caller asks for a report: a short
///     provenance header, then the <see cref="IssueMarkdownFormatter" /> rendering at
///     <see cref="DetailLevel.Full" /> — every issue on its own line with its own message, which is exactly
///     what the response's reduction ladder gives up on a solution-wide run. The caller passes that rendering
///     in rather than the issues, so the same string serves the response and the file instead of being
///     rendered twice.
/// </summary>
/// <remarks>
///     <para>
///         The header is here rather than inside <see cref="IssueMarkdownFormatter" /> because that
///         formatter's output is pinned as this repo's spec at every level, and the response must keep
///         rendering byte-for-byte as it does today. It is worth the separate class: a report is read minutes
///         or days after the call, often by a different agent than the one that asked for it, and
///         <c>Found N issue(s) across M file(s)</c> alone does not say which solution, at which severity, or
///         over which scope.
///     </para>
///     <para>
///         The timestamp is passed in rather than read from the clock here, so this stays a pure function of
///         its arguments like the rest of <c>Formatting/</c>. Output is <c>\n</c>-only, matching the
///         formatter it wraps.
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
            $"- Generated: {generatedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC",
            "",
            ""
        ];

        return string.Join("\n", header) + fullListing;
    }

    /// <summary>
    ///     What the run was narrowed to, spelled as the caller gave it. A scan of everything says so outright
    ///     rather than leaving the line blank, because "no scope" and "scope I forgot to record" read the same
    ///     on an empty value.
    /// </summary>
    private static string DescribeScope(IReadOnlyList<string>? scope)
    {
        return scope is { Count: > 0 } ? string.Join(", ", scope) : "whole solution";
    }
}