namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     Represents a rendering and the <see cref="DetailLevel" /> it settled at.
/// </summary>
/// <remarks>
///     The level is returned for the caller to log rather than logged here, because <c>Formatting/</c> stays
///     pure.
/// </remarks>
internal sealed record ProgressiveRendering(string Text, DetailLevel Level);

/// <summary>
///     Renders structured data at progressively lower <see cref="DetailLevel" />s until the formatted
///     output fits a character budget, avoiding a hard mid-response chop.
/// </summary>
/// <remarks>
///     The budget is always a parameter, never read from a static, so every environment read stays behind the
///     <c>IEnvironment</c> seam.
/// </remarks>
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
    ///     within <paramref name="maxChars" /> characters.
    /// </summary>
    /// <remarks>
    ///     <paramref name="startLevel" /> is a cap, not a pin: levels above it are skipped, and the walk still
    ///     steps below it when a rendering does not fit. <paramref name="describeReduction" /> words each
    ///     domain's reduction note, a generic message standing in when it is <see langword="null" />, and an
    ///     empty string from it means nothing was reduced at that level, so no note is emitted.
    /// </remarks>
    /// <returns>
    ///     The first rendering that fits, with the level it settled at. A reduced level's
    ///     <c>--- DETAIL REDUCED ---</c> note counts toward the fit. When nothing fits, the smallest rendering
    ///     and its note, which <c>ResponseTruncator</c> then cuts to the budget.
    /// </returns>
    public static ProgressiveRendering Render<T>(
        T data,
        Func<T, DetailLevel, string> format,
        int maxChars,
        Func<DetailLevel, string>? describeReduction = null,
        DetailLevel startLevel = DetailLevel.Full)
    {
        Func<DetailLevel, string> describe = describeReduction ?? DefaultReductionDescription;

        string previousOutput = null!;

        // Seeded at the cap rather than at Full: nothing above it is ever rendered, so naming Full in the
        // failsafe's note would label content no level produced.
        DetailLevel lastTriedLevel = startLevel;

        // The enum is ordered most-to-least detail, so "greater than or equal to" is "no more detailed
        // than the cap".
        foreach (DetailLevel level in ReductionOrder.Where(candidate => candidate >= startLevel))
        {
            string output = format(data, level);
            lastTriedLevel = level;

            // Skip a level that produced the same output as the previous — no point reporting a reduction
            // that changed nothing. Compare content, not length: two distinct levels can share a length, and
            // a length-collision skip would leave previousOutput stale while lastTriedLevel advanced, so the
            // failsafe would return an earlier level's content under a later level's label.
            if (output == previousOutput) continue;

            // The reduction note counts toward the budget: a rendering that fits only without its note
            // would be pushed over maxChars by appending it, handing the downstream truncator the mid-chop
            // this renderer exists to prevent.
            string candidate = AppendReductionNote(output, level, maxChars, describe, startLevel);
            if (candidate.Length <= maxChars) return new ProgressiveRendering(candidate, level);

            previousOutput = output;
        }

        // Nothing fit — return the smallest rendering for the hard-truncation failsafe.
        return new ProgressiveRendering(
            AppendReductionNote(previousOutput, lastTriedLevel, maxChars, describe, startLevel), lastTriedLevel);
    }

    private static string DefaultReductionDescription(DetailLevel level)
    {
        return "lower-signal detail was collapsed to fit the output budget.";
    }

    /// <summary>
    ///     Appends the reduction note, led by whether the caller asked for this level or the budget forced it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A level the caller asked for is not a budget failure, so "output exceeded the limit" would be
    ///         false there. Both forms carry the <c>--- DETAIL REDUCED ---</c> marker, because it is what an
    ///         agent matches on to tell a reduction from a full listing.
    ///     </para>
    ///     <para>
    ///         <see cref="DetailLevel.Full" /> gets no note, asked for or not, since nothing above it exists to
    ///         reduce from. An empty <paramref name="describe" /> is the domain saying this result lost nothing at
    ///         this level (a zero-issue inspect renders the same at every level), and only the domain can know
    ///         that: below a cap the level above was never rendered, and rendering it to compare is the work the
    ///         cap exists to avoid.
    ///     </para>
    /// </remarks>
    private static string AppendReductionNote(
        string output, DetailLevel level, int maxChars, Func<DetailLevel, string> describe, DetailLevel startLevel)
    {
        if (level == DetailLevel.Full) return output;

        string description = describe(level);
        if (description.Length == 0) return output;

        string lead = level == startLevel
            ? $"Rendered at the requested detail level {level}"
            : $"Output exceeded the {maxChars:N0} character limit. Reduced to {level}";

        return $"{output}\n\n--- DETAIL REDUCED ---\n{lead}: {description}";
    }
}