using System.Globalization;
using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Formatting;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     What the cache home holds for one solution in the moment before <c>jb</c> opens it, as one sentence a
///     log line can carry.
/// </summary>
/// <remarks>
///     <para>
///         This is the single best predictor of what a run is about to cost — a warm generation finishes in
///         well under a minute where a cold one can pass the run cap — and until it was written down the
///         answer was unrecoverable after the fact. A 552-second inspect in the field log could have been
///         cold, seeded, or queued behind another session, and nothing distinguished them.
///     </para>
///     <para>
///         Every reading is best effort and none of them can fail the run they describe: a cache home this
///         server cannot enumerate reports <see cref="Unreadable" /> and the run proceeds exactly as it would
///         have. The three facts are read together because it is their combination that means something —
///         a generation with no warm marker beside it is the part-built remnant of a killed run, which is
///         neither warm nor quite cold, and is the state <c>CacheTransplanter</c> exists to replace.
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
///     unvalidated until <c>jb</c> opens it and stamping a marker for it would be a lie. Without this the most
///     interesting state a run can start in would be reported as the one it is least like.
/// </param>
/// <param name="LastComparableCost">
///     What the last run starting from this same <see cref="CostBand" /> took, or <see langword="null" /> when
///     none has finished or the state falls in no band. Read from <see cref="JbCostRecord" /> by
///     <see cref="Read" /> rather than passed in, so every caller of the summary gets the figure without
///     knowing the record exists.
/// </param>
internal sealed record JbCacheState(
    IReadOnlyList<string>? Generations,
    TimeSpan? WarmMarkerAge,
    bool ResetRecorded,
    bool Seeded,
    TimeSpan? LastComparableCost = null)
{
    /// <summary>What every reading that could not be made looks like.</summary>
    private static readonly JbCacheState Unreadable = new(null, null, false, false);

    /// <summary>
    ///     The state as one readable clause — <c>warm (14m old marker, _App.123.00)</c> and the like, carrying
    ///     what a run like this one last cost where that is recorded. A rendered string rather than a handful
    ///     of separate log properties on purpose: this rides a line that already names the subcommand, the
    ///     solution and the queue wait, and it is read by a person scanning a file rather than by a query.
    /// </summary>
    internal string Summary
    {
        get
        {
            if (Generations is null) return "cache state unreadable";

            string cost = RecordedCost();

            if (Generations.Count == 0)
                return ResetRecorded ? $"cold after a reset (none on disk{cost})" : $"cold (none on disk{cost})";

            string directories = string.Join(", ", Generations);

            // Before the marker check, not after: a seed deliberately leaves no marker, so the two are
            // indistinguishable on disk and only the transplant knows which of them this is.
            if (Seeded) return $"seeded from a sibling checkout ({directories}{cost}), and this run re-keys it";

            if (WarmMarkerAge is not { } age)
                return $"part-built ({directories}, no warm marker — a run against it was killed)";

            return $"warm ({FormatAge(age)} old marker, {directories}{cost})";
        }
    }

    /// <summary>
    ///     Which recorded figure this state may be compared against, or <see langword="null" /> when it may be
    ///     compared against none. The arms are <see cref="Summary" />'s own, in the same order, because the
    ///     two answer one question: a state the summary describes in its own words is a state whose duration
    ///     only runs described the same way predict.
    /// </summary>
    /// <remarks>
    ///     Two states have no band. An unreadable cache home knows nothing, and a part-built generation is the
    ///     remnant of a run that was killed — how much of the work survived depends on when it died, so two
    ///     resumptions are not comparable and quoting one at the other would be a guess dressed as a
    ///     measurement.
    /// </remarks>
    internal JbCostBand? CostBand
    {
        get
        {
            if (Generations is null) return null;
            if (Generations.Count == 0) return JbCostBand.Cold;
            if (Seeded) return JbCostBand.Seeded;

            return WarmMarkerAge is null ? null : JbCostBand.Warm;
        }
    }

    /// <summary>
    ///     The state of <paramref name="solutionPath" />'s cache under <paramref name="cacheHome" /> right
    ///     now. Cheap: a single directory enumeration and three file stats — the third read only where the
    ///     state falls in a band, and a few bytes when it is — against a <c>jb</c> run measured in minutes.
    /// </summary>
    internal static JbCacheState Read(string solutionPath, string cacheHome, bool seeded, ILogger logger)
    {
        try
        {
            List<string> owned = JbCacheGenerations.FindFor(cacheHome, solutionPath).Owned
                .Select(generation => generation.Name)
                .ToList();

            JbCacheState state = new(
                owned,
                JbWarmMarker.Age(solutionPath, cacheHome, logger),
                JbColdTombstone.Exists(solutionPath, cacheHome, logger),
                seeded);

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
    ///     Total bytes and file count across the generations on disk, or <see langword="null" /> when there
    ///     are none or the walk failed. Separate from <see cref="Read" /> and reached only under
    ///     <c>Debug</c>, because unlike everything else here it is a full recursive walk of a directory tree
    ///     that runs to hundreds of megabytes.
    /// </summary>
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
    ///     What a run like this one last cost, as a clause to close the parenthetical with, or the empty
    ///     string when nothing comparable is recorded. Both halves have to hold — a band to key by and a
    ///     figure under it — so a state with no band quotes nothing even if handed a duration, and every arm
    ///     reads byte for byte as it did before any figure existed.
    /// </summary>
    private string RecordedCost()
    {
        if (CostBand is not { } band || LastComparableCost is not { } cost) return string.Empty;

        return $"; the last {JbCostRecord.Label(band)} run took {DurationFormatter.Format(cost)}";
    }

    /// <summary>
    ///     A duration at the coarsest unit that still says something — <c>42s</c>, <c>14m</c>, <c>3.2h</c>,
    ///     <c>6.1d</c>. A negative value is rendered as one rather than hidden: it means the marker is dated
    ///     into the future, which is a moved clock or a cache home carried between machines, and the reader is
    ///     better served seeing it.
    /// </summary>
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