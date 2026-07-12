namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     Renders structured data at progressively lower <see cref="DetailLevel" />s until the formatted
///     output fits a character budget, avoiding a hard mid-response chop. Ported from roz's
///     <c>ProgressiveRenderer</c>, with one deliberate deviation: roz reads the budget from a static
///     <c>ResponseTruncator.MaxChars</c>, but this server routes every environment read through the
///     <c>IEnvironment</c> seam, so the budget is threaded in as the <c>maxChars</c> parameter rather than
///     read from a static. There is intentionally no static-budget convenience overload.
/// </summary>
internal static class ProgressiveRenderer
{
    private static readonly DetailLevel[] ReductionOrder =
    [
        DetailLevel.Full,
        DetailLevel.High,
        DetailLevel.Medium,
        DetailLevel.Low,
        DetailLevel.Minimal
    ];

    /// <summary>
    ///     Renders <paramref name="data" /> at progressively lower detail levels until the output fits
    ///     within <paramref name="maxChars" />.
    /// </summary>
    /// <typeparam name="T">The structured result type to format.</typeparam>
    /// <param name="data">The structured result to format.</param>
    /// <param name="format">Renders <paramref name="data" /> at a given <see cref="DetailLevel" />.</param>
    /// <param name="maxChars">The maximum allowed response length, in characters.</param>
    /// <param name="describeReduction">
    ///     Optional per-level description appended to the reduction note; a generic message is used when
    ///     <see langword="null" />. Lets each domain (cleanup now, inspect later) explain its own reduction.
    /// </param>
    /// <returns>
    ///     The first level whose rendering fits within <paramref name="maxChars" /> —
    ///     <see cref="DetailLevel.Full" /> verbatim, lower levels including their appended
    ///     <c>--- DETAIL REDUCED ---</c> note in the fit check. If nothing fits, the smallest rendering plus
    ///     the note — the char-level truncation failsafe (<c>ResponseTruncator</c>) handles the rest.
    /// </returns>
    public static string Render<T>(
        T data,
        Func<T, DetailLevel, string> format,
        int maxChars,
        Func<DetailLevel, string>? describeReduction = null)
    {
        var describe = describeReduction ?? DefaultReductionDescription;

        string previousOutput = null!;
        var lastTriedLevel = DetailLevel.Full;

        foreach (DetailLevel level in ReductionOrder)
        {
            string output = format(data, level);
            lastTriedLevel = level;

            // Skip a level that produced the same output as the previous — no point reporting a reduction
            // that changed nothing. Compare content, not length: two distinct levels can share a length, and
            // a length-collision skip would leave previousOutput stale while lastTriedLevel advanced, so the
            // failsafe would return an earlier level's content under a later level's label.
            if (output == previousOutput) continue;

            // The reduction note counts toward the budget: a rendering that fits only without its note
            // would be pushed over maxChars by appending it, handing the downstream truncator exactly the
            // mid-chop this renderer exists to prevent.
            string candidate = level == DetailLevel.Full
                ? output
                : AppendReductionNote(output, level, maxChars, describe);
            if (candidate.Length <= maxChars) return candidate;

            previousOutput = output;
        }

        // Nothing fit — return the smallest rendering for the hard-truncation failsafe.
        return AppendReductionNote(previousOutput, lastTriedLevel, maxChars, describe);
    }

    private static string DefaultReductionDescription(DetailLevel level)
    {
        return "lower-signal detail was collapsed to fit the output budget.";
    }

    private static string AppendReductionNote(
        string output, DetailLevel level, int maxChars, Func<DetailLevel, string> describe)
    {
        return $"{output}\n\n--- DETAIL REDUCED ---\nOutput exceeded the {maxChars:N0} character limit. Reduced to {level}: {describe(level)}";
    }
}