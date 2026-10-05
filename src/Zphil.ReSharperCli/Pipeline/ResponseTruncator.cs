using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Pipeline;

/// <summary>
///     Caps a tool response's character count so a large inspection result can't exhaust the client's
///     context window.
/// </summary>
/// <remarks>
///     Truncation cuts at the last line boundary before the cap and appends a footer saying how much was
///     dropped, plus a caller-supplied hint on how to get a smaller result. It is the last-resort backstop: a
///     response rendered through <see cref="ProgressiveRenderer" /> only reaches here when even the smallest
///     rendering overflows.
/// </remarks>
internal static class ResponseTruncator
{
    /// <summary>The client-set token budget this server's output must fit inside.</summary>
    internal const string MaxTokensVariable = "MAX_MCP_OUTPUT_TOKENS";

    private const int DefaultMaxChars = 25_000;
    private const double CharsPerToken = 2.5;

    // The floor under a body budget once a prefix is deducted, itself capped by the budget being deducted
    // from. Only a pathological MAX_MCP_OUTPUT_TOKENS reaches it; it exists so the deduction can never hand
    // ProgressiveRenderer a negative limit to print.
    private const int MinimumBodyChars = 500;

    /// <summary>
    ///     Resolves the character cap from the MCP client's <c>MAX_MCP_OUTPUT_TOKENS</c> budget.
    /// </summary>
    /// <remarks>Falls back to <see cref="DefaultMaxChars" /> when the value is unset, blank, or non-positive.</remarks>
    internal static int ComputeMaxChars(string? maxMcpOutputTokens)
    {
        if (int.TryParse(maxMcpOutputTokens, out int tokens) && tokens > 0) return (int)(tokens * CharsPerToken);

        return DefaultMaxChars;
    }

    /// <summary>
    ///     <see cref="ComputeMaxChars(string?)" /> over the <see cref="MaxTokensVariable" /> read from
    ///     <paramref name="environment" /> — the one spelling of that lookup, shared by every call site.
    /// </summary>
    internal static int ComputeMaxChars(IEnvironment? environment)
    {
        return ComputeMaxChars(environment?.GetVariable(MaxTokensVariable));
    }

    /// <summary>
    ///     The budget left for the rendered body when <paramref name="prefix" /> is prepended to it.
    /// </summary>
    /// <remarks>
    ///     Charging a warning banner to the budget before rendering is what puts it <em>outside</em>
    ///     <see cref="ProgressiveRenderer" />'s reduction ladder: it survives every step down to
    ///     <c>Minimal</c> — correct for a warning about a destructive fallback — while the total still fits,
    ///     so <see cref="TruncateIfNeeded" /> is no likelier to bite than without it.
    /// </remarks>
    internal static int BudgetForBody(int maxChars, string prefix)
    {
        // The floor is itself capped at maxChars, so an empty prefix returns the budget untouched: a result
        // with nothing to warn about must render byte-for-byte as if no banner existed.
        int floor = Math.Min(maxChars, MinimumBodyChars);
        return Math.Max(maxChars - prefix.Length, floor);
    }

    /// <summary>
    ///     Returns <paramref name="text" /> unchanged when it fits within <paramref name="maxChars" />;
    ///     otherwise returns a truncated copy with a "RESPONSE TRUNCATED" footer.
    /// </summary>
    /// <remarks>
    ///     The footer closes with <paramref name="hint" /> when one is given — the domain's own advice on
    ///     getting a smaller result, which this generic backstop cannot know itself.
    /// </remarks>
    public static string TruncateIfNeeded(string text, string hint, int maxChars)
    {
        if (text.Length <= maxChars) return text;

        int cutPoint = text.LastIndexOf('\n', maxChars - 1);
        if (cutPoint <= 0) cutPoint = maxChars;

        string truncated = text[..cutPoint];
        int droppedChars = text.Length - cutPoint;
        string suffix = hint.Length > 0 ? $" {hint}" : "";

        return $"{truncated}\n\n--- RESPONSE TRUNCATED ---\nOutput was {text.Length:N0} characters, limit is {maxChars:N0} ({droppedChars:N0} characters omitted).\nThe results above are incomplete.{suffix}";
    }
}