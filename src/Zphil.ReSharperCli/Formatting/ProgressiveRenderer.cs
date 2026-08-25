namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     A rendering and the <see cref="DetailLevel" /> it settled at.
/// </summary>
/// <remarks>
///     The level is returned rather than logged here, because <c>Formatting/</c> is pure by convention and a
///     logger threaded into it would be the first exception. It is worth returning at all because whether the
///     ladder has <em>ever</em> stepped down in the field is not currently knowable: a reduced response says
///     so to the agent that received it and nowhere else, so the mechanism could be dead and look identical to
///     one that never had to fire.
/// </remarks>
internal sealed record ProgressiveRendering(string Text, DetailLevel Level);

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
    /// <param name="startLevel">
    ///     The most detailed level to try — a cap, not a pin. Levels above it are skipped entirely, and the
    ///     walk still steps below it when the rendering does not fit. At the default
    ///     <see cref="DetailLevel.Full" /> nothing is skipped and the ladder behaves exactly as it did before
    ///     a caller could ask for a level.
    /// </param>
    /// <returns>
    ///     The first level whose rendering fits within <paramref name="maxChars" /> —
    ///     <see cref="DetailLevel.Full" /> verbatim, lower levels including their appended
    ///     <c>--- DETAIL REDUCED ---</c> note in the fit check — paired with the level it settled at. If
    ///     nothing fits, the smallest rendering plus the note — the char-level truncation failsafe
    ///     (<c>ResponseTruncator</c>) handles the rest.
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
            // would be pushed over maxChars by appending it, handing the downstream truncator exactly the
            // mid-chop this renderer exists to prevent.
            string candidate = level == DetailLevel.Full
                ? output
                : AppendReductionNote(output, level, maxChars, describe, startLevel);
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
    ///     Appends the note, led by whichever of the two things happened. A level the caller asked for is
    ///     not a budget failure, and saying "output exceeded the limit" there would be false; a level below
    ///     the cap is the budget forcing the ladder down, and reads exactly as it always has. The
    ///     <c>--- DETAIL REDUCED ---</c> marker stays the one anchor across both, because it is what an agent
    ///     matches on to know the response is a reduction rather than a full listing.
    /// </summary>
    private static string AppendReductionNote(
        string output, DetailLevel level, int maxChars, Func<DetailLevel, string> describe, DetailLevel startLevel)
    {
        string lead = level == startLevel
            ? $"Rendered at the requested detail level {level}"
            : $"Output exceeded the {maxChars:N0} character limit. Reduced to {level}";

        return $"{output}\n\n--- DETAIL REDUCED ---\n{lead}: {describe(level)}";
    }
}