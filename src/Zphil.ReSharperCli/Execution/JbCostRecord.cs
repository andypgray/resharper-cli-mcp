using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     The cache state a <c>jb</c> run started from, reduced to the only distinction its duration turns on.
/// </summary>
/// <remarks>
///     Three bands rather than one number, because one number would lie. Measured on a single solution: 497
///     seconds cold, 456 seeded, 39 warm. A run that quoted a remembered figure without saying which of those
///     it came from would tell a warm caller to expect eight minutes, which is worse than saying nothing.
///     Two states have no band at all — an unreadable cache home, and the part-built remnant of a killed run —
///     and that is the same judgement pointed the other way: two resumptions of differently killed runs are
///     not comparable, so neither may quote the other.
/// </remarks>
internal enum JbCostBand
{
    /// <summary>No cache generation on disk, whether or not a reset is what emptied it.</summary>
    Cold,

    /// <summary>A generation <c>CacheTransplanter</c> had just copied from a sibling checkout.</summary>
    Seeded,

    /// <summary>A generation of this solution's own that some run has already finished against.</summary>
    Warm
}

/// <summary>
///     How long the last <c>jb</c> run against this solution took, per <see cref="JbCostBand" />, in a file
///     beside the warm marker. The heartbeat says how long a run has been going against the cap; this is what
///     lets the same line say how long a run like it usually takes, which is the other half of telling slow
///     from stuck.
/// </summary>
/// <remarks>
///     <para>
///         Its failure direction is <see cref="JbWarmMarker" />'s rather than
///         <see cref="JbColdTombstone" />'s, and the difference is worth stating: a lost stamp costs a
///         missing hint, while a lost tombstone risks a promise to the user going unkept. So every failure
///         here — an unwritable cache home, a file this build cannot parse, a key that cannot be derived —
///         lands at <c>Debug</c> and degrades to "no figure", which renders as the line that was there before
///         this file existed.
///     </para>
///     <para>
///         <see cref="Stamp" /> is read-modify-write and keeps every line it did not recognise, so a band a
///         later build records does not lose its figure to an earlier one running beside it in the same cache
///         home. There is no locking, and none is needed: every read and write here happens under the
///         solution's <see cref="JbRunLock" /> lease — the stamp under the runner's, the clear under the
///         reset's, and the read inside <see cref="JbCacheState.Read" />, which runs under the same lease.
///         The <see cref="FileShare.ReadWrite" /> on the reads is for the cache home's other occupants
///         rather than for this file's own callers.
///     </para>
/// </remarks>
internal static class JbCostRecord
{
    private const string Extension = "cost";

    /// <summary>
    ///     Where the record for one cache generation lives: beside the lock file, the warm marker and the
    ///     cold tombstone, under <see cref="JbSidecar" />'s one key for the generation.
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
            JbCostBand.Warm => "warm",
            _ => throw new ArgumentOutOfRangeException(nameof(band), band, "Unmapped jb cost band.")
        };
    }

    /// <summary>
    ///     Record that a run starting from <paramref name="band" /> took <paramref name="cost" />, replacing
    ///     whatever that band last cost and leaving every other band's figure where it was.
    /// </summary>
    /// <remarks>
    ///     Whole seconds, because that is the resolution every reader renders at and a figure with more of
    ///     them in the file than in the sentence invites a diff that means nothing.
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
    ///     Forget every figure recorded for this solution — for a cache reset, which has just ended the
    ///     lineage they describe. Swallows failures the way <see cref="Stamp" /> does, and clearing a record
    ///     that was never written is not one of them.
    /// </summary>
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
    ///     something this build cannot read as one. <see cref="NumberStyles.None" /> is the whole guard: it
    ///     refuses a sign, a separator and surrounding space, so a hand-edited or half-written file quotes
    ///     nothing rather than quoting nonsense.
    /// </summary>
    private static TimeSpan? Seconds(string line, string label)
    {
        string value = line[(label.Length + 1)..];

        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }
}