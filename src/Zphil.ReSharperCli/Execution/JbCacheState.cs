using System.Globalization;
using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Formatting;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Represents what the cache home holds for one solution in the moment before <c>jb</c> opens it, as one
///     clause a log line can carry.
/// </summary>
/// <remarks>
///     <para>
///         The state predicts what a run is about to cost, and it cannot be recovered after the fact: without
///         it, a long inspect in the log could have been cold, seeded, or queued behind another session, and
///         nothing would distinguish them.
///     </para>
///     <para>
///         Every reading is best effort and none of them can fail the run they describe: a cache home this
///         server cannot enumerate reports <see cref="Unreadable" /> and the run proceeds as it would have.
///         The readings are taken together because it is their combination that means something: a
///         generation with no warm marker beside it is the part-built remnant of a killed run, which is
///         neither warm nor quite cold.
///     </para>
/// </remarks>
/// <param name="Generations">
///     The generation directories this solution's path owns, or <see langword="null" /> when the cache home
///     could not be read.
/// </param>
/// <param name="WarmMarkerAge">
///     How long ago a <c>jb</c> run against this generation last succeeded, or <see langword="null" /> when
///     none ever did.
/// </param>
/// <param name="ResetRecorded">Whether a cache reset asked for the next run to be cold.</param>
/// <param name="Seeded">
///     Whether <c>CacheTransplanter</c> just planted this generation by copying a sibling checkout's. It has to
///     be told rather than read, because a seeded generation is indistinguishable on disk from the part-built
///     remnant of a killed run: both are directories with no warm marker beside them, since the copy is
///     unvalidated until <c>jb</c> opens it.
/// </param>
/// <param name="LastComparableCost">
///     What the last run starting from this same <see cref="CostBand" /> took, or <see langword="null" /> when
///     none has finished or the state falls in no band. <see cref="Read" /> fills it from
///     <see cref="JbCostRecord" />, so no caller of the summary has to know the record exists.
/// </param>
/// <param name="MarkerJbVersion">
///     Which <c>jb</c> build the warm marker says left this generation warm, or <see langword="null" /> when
///     it names none.
/// </param>
/// <param name="CurrentJbVersion">
///     Which <c>jb</c> build is about to open it, or <see langword="null" /> when the caller cannot say, which
///     switches the staleness judgement off (<see cref="JbWarmMarker.WrittenByAnotherBuild" />).
/// </param>
internal sealed record JbCacheState(
    IReadOnlyList<string>? Generations,
    TimeSpan? WarmMarkerAge,
    bool ResetRecorded,
    bool Seeded,
    TimeSpan? LastComparableCost = null,
    string? MarkerJbVersion = null,
    string? CurrentJbVersion = null)
{
    /// <summary>What every reading that could not be made looks like.</summary>
    private static readonly JbCacheState Unreadable = new(null, null, false, false);

    /// <summary>
    ///     Gets the state as one readable clause, such as <c>warm (14m old marker, _App.123.00)</c>, carrying
    ///     what a run like this one last cost where that is recorded.
    /// </summary>
    /// <remarks>
    ///     A rendered string rather than separate log properties, because it is read by a person scanning a log
    ///     file rather than by a query.
    /// </remarks>
    internal string Summary
    {
        get
        {
            if (Generations is null) return "cache state unreadable";

            string cost = RecordedCost();

            if (Generations.Count == 0)
                return ResetRecorded ? $"cold after a reset (none on disk{cost})" : $"cold (none on disk{cost})";

            string directories = string.Join(", ", Generations);

            // Before the marker check: a seeded generation has no marker either, and only the transplant knows
            // which of the two this is.
            if (Seeded) return $"seeded from a sibling checkout ({directories}{cost}), and this run re-keys it";

            if (WarmMarkerAge is not { } age)
                return $"part-built ({directories}, no warm marker — a run against it was killed)";

            // Ahead of warm, because this generation has every mark of being warm except the one that
            // decides what it costs: jb validates a cache against its own format and rebuilds it in place,
            // so a cache another build wrote buys the run nothing.
            if (WrittenByAnotherJb)
            {
                string writer = MarkerJbVersion is null ? "an earlier jb" : $"jb {MarkerJbVersion}";
                return $"stale (cache written by {writer}, this is {CurrentJbVersion}, and jb rebuilds it{cost})";
            }

            return $"warm ({FormatAge(age)} old marker, {directories}{cost})";
        }
    }

    /// <summary>
    ///     Gets the band whose recorded figure this state may be compared against, or <see langword="null" />
    ///     when it may be compared against none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The arms are <see cref="Summary" />'s own, in the same order, and must stay so: a state the
    ///         summary describes in its own words is a state whose duration only runs described the same way
    ///         predict.
    ///     </para>
    ///     <para>
    ///         An unreadable cache home, a part-built generation and a warm one have no band. The first knows
    ///         nothing; how much of a killed run's work survived depends on when it died, so two resumptions are
    ///         not comparable; and why a warm figure predicts nothing is on <see cref="JbCostBand" />. The
    ///         summary still describes a warm state as warm, and only the figure is withheld.
    ///     </para>
    ///     <para>
    ///         A stale generation bands as <see cref="JbCostBand.Cold" />, because <c>jb</c> rebuilds a cache
    ///         another build wrote and the run does cold-shaped work.
    ///     </para>
    /// </remarks>
    internal JbCostBand? CostBand
    {
        get
        {
            if (Generations is null) return null;
            if (Generations.Count == 0) return JbCostBand.Cold;
            if (Seeded) return JbCostBand.Seeded;
            if (WarmMarkerAge is null) return null;

            return WrittenByAnotherJb ? JbCostBand.Cold : null;
        }
    }

    /// <summary>
    ///     Gets a value indicating whether the build that left this generation warm is not the one about to open
    ///     it.
    /// </summary>
    private bool WrittenByAnotherJb => JbWarmMarker.WrittenByAnotherBuild(MarkerJbVersion, CurrentJbVersion);

    /// <summary>
    ///     Gets the figure this state may quote and the band that keys it, or <see langword="null" /> when it may
    ///     quote none.
    /// </summary>
    /// <remarks>
    ///     Quoting needs both a band and a figure under it, so a state handed a duration with no band to key it
    ///     by still quotes nothing. This is the one eligibility rule for quoting a figure, and a surface that
    ///     quotes one goes through it.
    /// </remarks>
    internal (JbCostBand Band, TimeSpan Cost)? QuotableCost =>
        CostBand is { } band && LastComparableCost is { } cost
            ? (band, cost)
            : null;

    /// <summary>
    ///     Reads the state of <paramref name="solutionPath" />'s cache under <paramref name="cacheHome" /> right
    ///     now, as <paramref name="jbVersion" /> is about to open it.
    /// </summary>
    /// <remarks>
    ///     Cheap beside the <c>jb</c> run it precedes: one directory enumeration and a few small file reads, the
    ///     recorded cost read only where the state falls in a band.
    /// </remarks>
    internal static JbCacheState Read(
        string solutionPath,
        string cacheHome,
        bool seeded,
        string? jbVersion,
        ILogger logger)
    {
        try
        {
            List<string> owned = JbCacheGenerations.FindFor(cacheHome, solutionPath).Owned
                .Select(generation => generation.Name)
                .ToList();

            string markerPath = JbWarmMarker.PathFor(solutionPath, cacheHome);

            JbCacheState state = new(
                owned,
                JbWarmMarker.Age(solutionPath, cacheHome, logger),
                JbColdTombstone.Exists(solutionPath, cacheHome, logger),
                seeded,
                MarkerJbVersion: JbWarmMarker.TryReadJbVersion(markerPath, logger),
                CurrentJbVersion: jbVersion);

            // After the rest is settled, because the band the figure is keyed by is derived from it.
            if (state.CostBand is not { } band) return state;

            return state with { LastComparableCost = JbCostRecord.TryRead(solutionPath, cacheHome, band, logger) };
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            logger.LogDebug(exception, "Could not read the cache state for solution {SolutionPath} in cache home {CacheHome}", solutionPath, cacheHome);
            return Unreadable;
        }
    }

    /// <summary>
    ///     Measures the total bytes and file count across the generations on disk, or returns
    ///     <see langword="null" /> when there are none or the walk failed.
    /// </summary>
    /// <remarks>
    ///     Separate from <see cref="Read" />, and meant only for a caller logging at <c>Debug</c>, because
    ///     unlike everything else here it is a full recursive walk of every generation's directory tree.
    /// </remarks>
    internal (long Bytes, int Files)? TryMeasure(string cacheHome)
    {
        if (Generations is null or { Count: 0 }) return null;

        try
        {
            long bytes = 0;
            var files = 0;
            foreach (string generation in Generations)
            foreach (FileInfo file in new DirectoryInfo(JbCacheGenerations.PathUnder(cacheHome, generation))
                         .EnumerateFiles("*", SearchOption.AllDirectories))
            {
                bytes += file.Length;
                files++;
            }

            return (bytes, files);
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            return null;
        }
    }

    /// <summary>
    ///     Returns what a run like this one last cost, as a clause to close the parenthetical with, or the empty
    ///     string when <see cref="QuotableCost" /> permits none.
    /// </summary>
    /// <remarks>The empty string keeps every arm reading byte for byte the same when no figure is recorded.</remarks>
    private string RecordedCost()
    {
        if (QuotableCost is not { } quotable) return string.Empty;

        return $"; the last {JbCostRecord.Label(quotable.Band)} run took {DurationFormatter.Format(quotable.Cost)}";
    }

    /// <summary>
    ///     Formats a duration at the coarsest unit that still says something: <c>42s</c>, <c>14m</c>,
    ///     <c>3.2h</c>, <c>6.1d</c>.
    /// </summary>
    /// <remarks>
    ///     A negative value is rendered as one rather than hidden: it means the marker is dated into the future,
    ///     which is a moved clock or a cache home carried between machines, and the reader is better served
    ///     seeing it.
    /// </remarks>
    private static string FormatAge(TimeSpan age)
    {
        TimeSpan magnitude = age < TimeSpan.Zero ? -age : age;

        if (magnitude < TimeSpan.FromMinutes(1)) return Format(age.TotalSeconds, "0", "s");
        if (magnitude < TimeSpan.FromHours(1)) return Format(age.TotalMinutes, "0", "m");
        if (magnitude < TimeSpan.FromDays(1)) return Format(age.TotalHours, "0.0", "h");

        return Format(age.TotalDays, "0.0", "d");
    }

    private static string Format(double value, string format, string unit)
    {
        return value.ToString(format, CultureInfo.InvariantCulture) + unit;
    }
}