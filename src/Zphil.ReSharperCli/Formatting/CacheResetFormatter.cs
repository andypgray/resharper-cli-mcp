using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     Renders a <see cref="CacheResetOutcome" /> as a plain-text report: what was dropped, what would not
///     go, and what the next call now costs. Output uses <c>\n</c> line endings and is ASCII-only, matching
///     the other formatters.
/// </summary>
/// <remarks>
///     Alone among the tool outputs this one carries no <see cref="DetailLevel" /> ladder, because it has no
///     axis to reduce along: a reset report is one line per cache generation for a single solution — a
///     handful at the very most — where an inspect or cleanup report grows with the codebase. A ladder here
///     would be pinned-by-tests ceremony over an output that cannot overflow, and <c>ResponseTruncator</c>
///     remains the backstop if a pathological budget ever proves that wrong.
/// </remarks>
internal static class CacheResetFormatter
{
    /// <summary>
    ///     Closes the truncation footer for this tool (via <c>ResharperTools.TruncationHintFor</c>). A report
    ///     cut short must not read as fewer generations deleted — the directories are already gone.
    /// </summary>
    internal const string ResetRanInFull = "The reset itself completed; only the report was cut short.";

    private const string ColdNextCall =
        "The next inspect or cleanup against this solution rebuilds the cache from cold, which can take minutes.";

    public static string Format(CacheResetOutcome outcome)
    {
        List<string> lines = [];

        if (outcome.Dropped.Count > 0)
        {
            lines.Add($"Dropped {outcome.Dropped.Count} ReSharper cache generation(s) for \"{outcome.SolutionPath}\" under \"{outcome.CacheHome}\":");
            lines.AddRange(outcome.Dropped.Select(name => $"  - {name}"));
        }
        else if (outcome.Failures.Count == 0)
        {
            // Nothing was found, or only neighbours were. Either way nothing here was this solution's to
            // drop, which is a different claim from an empty cache home and is reported as itself.
            lines.Add(NothingFound(outcome));
        }

        if (outcome.Failures.Count > 0)
        {
            lines.Add($"Could not drop {outcome.Failures.Count} generation(s):");

            // The reason is whatever the filesystem said, and some of its messages span lines; each failure
            // gets one list item, so the flattening is this report's layout rule and is applied here.
            lines.AddRange(outcome.Failures.Select(failure => $"  - {failure.Name}: {ConfigWarningBanner.SingleLine(failure.Reason)}"));
            lines.Add(
                "A generation that will not delete is usually one another jb still has open. Retry once it has "
                + "finished; this tool is safe to run again.");
        }

        if (outcome.LeftAlone.Count > 0)
        {
            lines.Add(
                $"Left {outcome.LeftAlone.Count} generation(s) alone, whose names hash to a different solution path "
                + "— another checkout or copy of a solution with this file name:");
            lines.AddRange(outcome.LeftAlone.Select(name => $"  - {name}"));
        }

        // Only true if something actually went: a reset that dropped nothing left the cache exactly as warm
        // (or as stale) as it found it, and saying otherwise would send the caller to wait out a cold run
        // that is not going to happen.
        if (outcome.Dropped.Count > 0) lines.Add(ColdNextCall);

        return string.Join("\n", lines);
    }

    private static string NothingFound(CacheResetOutcome outcome)
    {
        return $"No ReSharper cache generation for \"{outcome.SolutionPath}\" was found under \"{outcome.CacheHome}\". "
               + "Nothing to drop, so the next inspect or cleanup builds the cache from cold anyway.";
    }
}