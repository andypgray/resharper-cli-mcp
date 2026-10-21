using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     Renders a <see cref="CacheResetOutcome" /> as a plain-text report: what was dropped, what would not
///     go, and what the next call now costs.
/// </summary>
/// <remarks>
///     It carries no <see cref="DetailLevel" /> ladder, because it has no axis to reduce along: a reset report
///     is one line per cache generation for a single solution, where an inspect or cleanup report grows with the
///     codebase. <c>ResponseTruncator</c> is its only backstop.
/// </remarks>
internal static class CacheResetFormatter
{
    /// <summary>
    ///     The sentence that closes a cut-short reset report, so it cannot read as fewer generations deleted:
    ///     the directories are already gone.
    /// </summary>
    internal const string ResetRanInFull = "The reset itself completed; only the report was cut short.";

    private const string ColdNextCall =
        "The next inspect or cleanup against this solution rebuilds the cache from cold, which can take minutes.";

    /// <summary>
    ///     How to act on a left-alone generation whose checkout is gone or cannot be named.
    /// </summary>
    /// <remarks>
    ///     Stated inline rather than pointed at, because an agent acts on a cure in the result it is reading and
    ///     passes over a pointer to one.
    /// </remarks>
    private const string ReclaimHint =
        "To reclaim the cache of a checkout that has been deleted, call this tool again with solutionPath "
        + "set to the path it had.";

    /// <summary>
    ///     What a reclaim closes with in place of <see cref="ColdNextCall" />, since there is no next call
    ///     against a path with no checkout on it.
    /// </summary>
    private const string NothingRebuildsThis =
        "The solution file does not exist, so nothing rebuilds this cache. A checkout created at that path "
        + "later starts like any other new one, seeded from a sibling checkout where one is warm.";

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
            lines.AddRange(outcome.LeftAlone.Select(Describe));

            // Only where something in the list is reclaimable or unnamed. A caller whose neighbours are all
            // live checkouts has nothing to do, and a cure printed under every report stops being read.
            if (outcome.LeftAlone.Any(NeedsReclaiming)) lines.Add(ReclaimHint);
        }

        // Only true if something went: a reset that dropped nothing left the cache as warm (or as stale) as
        // it found it, and saying otherwise would send the caller to wait out a cold run that is not going to
        // happen.
        if (outcome.Dropped.Count > 0) lines.Add(outcome.SolutionFileExists ? ColdNextCall : NothingRebuildsThis);

        return string.Join("\n", lines);
    }

    /// <summary>Formats one left-alone generation as its list item: the name, and whose it is.</summary>
    /// <remarks>
    ///     Each attribution reads differently because each tells the caller something different: a path that
    ///     still exists is someone's working checkout, a path that is gone is reclaimable, and one that cannot be
    ///     named is a fact about this server's own records rather than about the directory.
    /// </remarks>
    private static string Describe(LeftAloneGeneration generation)
    {
        return generation.Attribution switch
        {
            LeftAloneAttribution.CheckoutPresent =>
                $"  - {generation.Name}: last warmed for \"{generation.LastWarmedFor}\"",
            LeftAloneAttribution.CheckoutGone =>
                $"  - {generation.Name}: last warmed for \"{generation.LastWarmedFor}\", which no longer exists",
            LeftAloneAttribution.PathNotRecorded =>
                $"  - {generation.Name}: last warmed by a run that recorded no path",
            LeftAloneAttribution.NoRunOnRecord =>
                $"  - {generation.Name}: no successful run on record",
            _ => throw new ArgumentOutOfRangeException(
                nameof(generation), generation.Attribution, "Unmapped left-alone attribution.")
        };
    }

    /// <summary>
    ///     Determines whether the reclaim hint would help with this item: its checkout is gone, or this server
    ///     cannot say whose it is, which leaves the caller as the one who might know.
    /// </summary>
    private static bool NeedsReclaiming(LeftAloneGeneration generation)
    {
        return generation.Attribution != LeftAloneAttribution.CheckoutPresent;
    }

    private static string NothingFound(CacheResetOutcome outcome)
    {
        var opening = $"No ReSharper cache generation for \"{outcome.SolutionPath}\" was found under \"{outcome.CacheHome}\". ";

        // A reclaim that found nothing has nothing to say about a next call either: there is no checkout at
        // that path to make one.
        return outcome.SolutionFileExists
            ? opening + "Nothing to drop, so the next inspect or cleanup builds the cache from cold anyway."
            : opening + "Nothing to drop.";
    }
}