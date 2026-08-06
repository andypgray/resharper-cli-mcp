using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Pipeline;

/// <summary>
///     Caps a tool response's character count so a large inspection result can't exhaust the client's
///     context window. Truncation cuts at the last line boundary before the cap and appends a footer
///     saying how much was dropped, plus a tool-specific hint on how to get a smaller result. It is the
///     last-resort backstop: both tools render through <see cref="ProgressiveRenderer" /> first, so a
///     response only reaches here when even the smallest rendering overflows.
/// </summary>
internal static class ResponseTruncator
{
    private const int DefaultMaxChars = 25_000;
    private const double CharsPerToken = 2.5;

    // The floor under a body budget once a prefix is deducted, itself capped by the budget being deducted
    // from. Only a pathological MAX_MCP_OUTPUT_TOKENS reaches it; it exists so the deduction can never hand
    // ProgressiveRenderer a negative limit to print.
    private const int MinimumBodyChars = 500;

    /// <summary>
    ///     Resolves the character cap from the MCP client's <c>MAX_MCP_OUTPUT_TOKENS</c> budget
    ///     (× 2.5 chars/token), falling back to 25,000 when the value is unset, blank, or non-positive.
    /// </summary>
    internal static int ComputeMaxChars(string? maxMcpOutputTokens)
    {
        if (int.TryParse(maxMcpOutputTokens, out int tokens) && tokens > 0) return (int)(tokens * CharsPerToken);

        return DefaultMaxChars;
    }

    /// <summary>
    ///     The budget left for the rendered body when <paramref name="prefix" /> is prepended to it. Charging
    ///     a warning banner to the budget before rendering is what puts it <em>outside</em>
    ///     <see cref="ProgressiveRenderer" />'s reduction ladder: it survives every step down to
    ///     <c>Minimal</c> — correct for a warning about a destructive fallback — while the total still fits,
    ///     so <see cref="TruncateIfNeeded" /> is no likelier to bite than without it.
    /// </summary>
    internal static int BudgetForBody(int maxChars, string prefix)
    {
        // The floor is itself capped at maxChars, so an empty prefix returns the budget untouched: a result
        // with nothing to warn about must render byte-for-byte as it did before there was a banner at all.
        int floor = Math.Min(maxChars, MinimumBodyChars);
        return Math.Max(maxChars - prefix.Length, floor);
    }

    /// <summary>
    ///     Returns <paramref name="text" /> unchanged when it fits within <paramref name="maxChars" />;
    ///     otherwise returns a truncated copy with a "RESPONSE TRUNCATED" footer.
    /// </summary>
    public static string TruncateIfNeeded(string text, string? toolName, int maxChars)
    {
        if (text.Length <= maxChars) return text;

        int cutPoint = text.LastIndexOf('\n', maxChars - 1);
        if (cutPoint <= 0) cutPoint = maxChars;

        string truncated = text[..cutPoint];
        int droppedChars = text.Length - cutPoint;
        string hint = toolName == ResharperTools.InspectToolName ? $" {IssueMarkdownFormatter.NarrowingHint}" : "";

        return $"{truncated}\n\n--- RESPONSE TRUNCATED ---\nOutput was {text.Length:N0} characters, limit is {maxChars:N0} ({droppedChars:N0} characters omitted).\nThe results above are incomplete.{hint}";
    }
}