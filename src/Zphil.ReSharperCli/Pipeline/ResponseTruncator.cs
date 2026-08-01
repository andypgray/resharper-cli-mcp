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