using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     The cache state a <c>jb</c> run started from, reduced to the distinctions its duration turns on and
///     the last run like it can predict.
/// </summary>
/// <remarks>
///     <para>
///         Two bands rather than one number, because one number would lie: on one solution, cold and seeded
///         runs each took the better part of ten minutes and a warm one well under one. A run that quoted a
///         remembered figure without saying which of those it came from would tell a warm caller to expect
///         minutes it will not take, which is worse than saying nothing.
///     </para>
///     <para>
///         Warm is a state a run starts in and a line describes, and it is not a band. Measured, the figure
///         the previous warm run left does not predict the next one's duration — the error is the size of the
///         quantity. The cause is structural rather than a matter of tuning: a warm run's cost is set by how
///         much source changed since the last one, which nothing here observes, so the last figure is a
///         lag-one estimator of a series driven by something unobserved, and a running median over the
///         recorded runs does no better. Left out of the enum rather than gated out at each door, so that a
///         warm figure cannot be recorded or quoted by any caller rather than merely declined by the ones that
///         remembered to ask.
///     </para>
///     <para>
///         Two more states have no band, and that is the same judgement pointed the other way — an unreadable
///         cache home, and the part-built remnant of a killed run: two resumptions of differently killed runs
///         are not comparable, so neither may quote the other.
///     </para>
/// </remarks>
internal enum JbCostBand
{
    /// <summary>No cache generation on disk, whether or not a reset is what emptied it.</summary>
    Cold,

    /// <summary>A generation <c>CacheTransplanter</c> had just copied from a sibling checkout.</summary>
    Seeded
}

/// <summary>
///     How long the last <c>jb</c> run against this solution took, per <see cref="JbCostBand" />, in a file
///     beside the warm marker.
/// </summary>
/// <remarks>
///     <para>
///         The heartbeat says how long a run has been going against the cap; this is what lets the same line
///         say how long a run like it usually takes, which is the other half of telling slow from stuck.
///     </para>
///     <para>
///         Its failure direction is <see cref="JbWarmMarker" />'s rather than
///         <see cref="JbColdTombstone" />'s, and the difference is worth stating: a lost stamp costs a
///         missing hint, while a lost tombstone risks a promise to the user going unkept. So every failure
///         here — an unwritable cache home, a file this build cannot parse, a key that cannot be derived —
///         lands at <c>Debug</c> and degrades to "no figure", which renders as a line with no figure in it.
///     </para>
///     <para>
///         <see cref="Stamp" /> is read-modify-write and keeps every line it did not recognise, so a band a
///         later build records does not lose its figure to an earlier one running beside it in the same cache
///         home. There is no locking of its own, and none is needed: every read and write happens under the
///         solution's <see cref="JbRunLock" /> lease, which a caller must hold. The
///         <see cref="FileShare.ReadWrite" /> on the reads is for the cache home's other occupants rather than
///         for this file's own callers.
///     </para>
/// </remarks>
internal static class JbCostRecord
{
    private const string Extension = "cost";

    /// <summary>
    ///     Where the record for one cache generation lives: beside the other sidecars, under
    ///     <see cref="JbSidecar" />'s one key for the generation.
    /// </summary>
    internal static string PathFor(string solutionPath, string cacheHome)
    {
        return JbSidecar.PathFor(solutionPath, cacheHome, Extension);
    }

    /// <summary>
    ///     How a band is spelled — the one spelling, shared by the tokens in the file and by the prose that
    ///     quotes a figure, so a record written by one and read by the other cannot come to disagree.
    /// </summary>
    internal static string Label(JbCostBand band)
    {
        return band switch
        {
            JbCostBand.Cold => "cold",
            JbCostBand.Seeded => "seeded",
            _ => throw new ArgumentOutOfRangeException(nameof(band), band, "Unmapped jb cost band.")
        };
    }

    /// <summary>
    ///     Records that a run starting from <paramref name="band" /> took <paramref name="cost" />, replacing
    ///     whatever that band last cost and leaving every other band's figure where it was.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Whole seconds, because that is the resolution every reader renders at and a figure with more of
    ///         them in the file than in the sentence invites a diff that means nothing.
    ///     </para>
    ///     <para>
    ///         A <c>warm</c> line an earlier build wrote, from when that was a band, is left exactly where it
    ///         is rather than swept up — the rule this file keeps for any line it does not recognise. A reset
    ///         deletes the file outright, and until then nothing reads the line, because no band names it.
    ///     </para>
    /// </remarks>
    internal static void Stamp(string solutionPath, string cacheHome, JbCostBand band, TimeSpan cost, ILogger logger)
    {
        try
        {
            string label = Label(band);
            var seconds = (long)Math.Round(cost.TotalSeconds, MidpointRounding.AwayFromZero);

            List<string> lines = ExistingLines(PathFor(solutionPath, cacheHome));
            lines.RemoveAll(line => Records(line, label));
            lines.Add($"{label} {seconds.ToString(CultureInfo.InvariantCulture)}");

            using FileStream record = JbSidecar.OpenToWrite(solutionPath, cacheHome, Extension);
            record.Write(Encoding.UTF8.GetBytes(string.Join('\n', lines) + '\n'));
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            logger.LogDebug(
                exception,
                "Could not record what the jb run on solution {SolutionPath} cost in cache home {CacheHome}",
                solutionPath,
                cacheHome);
        }
    }

    /// <summary>
    ///     What the last run starting from <paramref name="band" /> cost, or <see langword="null" /> when no
    ///     comparable run has finished — nothing recorded, a record this build cannot read, or a line for
    ///     this band that is not a whole number of seconds.
    /// </summary>
    internal static TimeSpan? TryRead(string solutionPath, string cacheHome, JbCostBand band, ILogger logger)
    {
        try
        {
            string label = Label(band);

            return ExistingLines(PathFor(solutionPath, cacheHome))
                .Where(line => Records(line, label))
                .Select(line => Seconds(line, label))
                .FirstOrDefault(seconds => seconds is not null);
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            logger.LogDebug(
                exception,
                "Could not read what a jb run on solution {SolutionPath} last cost in cache home {CacheHome}",
                solutionPath,
                cacheHome);

            return null;
        }
    }

    /// <summary>
    ///     Forgets every figure recorded for this solution, whose lineage a cache reset has just ended.
    /// </summary>
    /// <remarks>
    ///     Swallows failures the way <see cref="Stamp" /> does, and clearing a record that was never written is
    ///     not one of them.
    /// </remarks>
    internal static void Clear(string solutionPath, string cacheHome, ILogger logger)
    {
        JbSidecar.TryDelete(solutionPath, cacheHome, Extension, "recorded jb run costs", logger);
    }

    /// <summary>
    ///     The file's lines, or none when nothing has been recorded yet — <see cref="JbSidecar.ReadLines" />,
    ///     byte for byte: no trim, because <see cref="Stamp" /> writes back every line it does not recognise
    ///     and must not rewrite another build's entries in passing.
    /// </summary>
    private static List<string> ExistingLines(string recordPath)
    {
        return JbSidecar.ReadLines(recordPath);
    }

    /// <summary>Whether <paramref name="line" /> is the entry for <paramref name="label" />'s band.</summary>
    private static bool Records(string line, string label)
    {
        return line.StartsWith(label + ' ', StringComparison.Ordinal);
    }

    /// <summary>
    ///     The duration <paramref name="line" /> carries, or <see langword="null" /> when it carries
    ///     something this build cannot read as one.
    /// </summary>
    /// <remarks>
    ///     <see cref="NumberStyles.None" /> is the whole guard: it refuses a sign, a separator and surrounding
    ///     space, so a hand-edited or half-written file quotes nothing rather than quoting nonsense.
    /// </remarks>
    private static TimeSpan? Seconds(string line, string label)
    {
        string value = line[(label.Length + 1)..];

        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }
}