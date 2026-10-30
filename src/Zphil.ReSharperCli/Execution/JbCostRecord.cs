using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Represents the cache state a <c>jb</c> run started from, reduced to the distinctions its duration turns
///     on and the last run like it can predict.
/// </summary>
/// <remarks>
///     <para>
///         Banded because a run's duration turns on the state it started from, so a remembered figure quoted
///         without its band would tell a warm caller to expect minutes it will not take, which is worse than
///         saying nothing.
///     </para>
///     <para>
///         Warm is a state a run starts in and a line describes, but not a band: a warm run's cost is set by
///         how much source changed since the last one, which nothing here observes, so the last warm figure
///         does not predict the next. It is absent from the enum rather than gated out at each door, so no
///         caller can record or quote a warm figure.
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
///     Records how long the last <c>jb</c> run against this solution took, per subcommand and
///     <see cref="JbCostBand" />, in a file beside the warm marker.
/// </summary>
/// <remarks>
///     <para>
///         The heartbeat says how long a run has been going against the cap; this lets the same line say how
///         long the last run like it took, which is the other half of telling slow from stuck.
///     </para>
///     <para>
///         Keyed by subcommand as well as band, because a cold cleanup and a cold inspection of one solution
///         differ by minutes, so one quoted to the other would mislead. An <c>inspectcode</c> figure is written
///         under the band alone, the way a version that recorded no subcommand wrote every figure, so a figure
///         such a version left is read as an inspection's, which nearly all of them are.
///     </para>
///     <para>
///         Every failure (an unwritable cache home, a file this build cannot parse, a key that cannot be
///         derived) lands at <c>Debug</c> and degrades to no figure, because a lost stamp costs a missing hint
///         and nothing more.
///     </para>
///     <para>
///         <see cref="Stamp" /> is read-modify-write and keeps every line it did not recognise, so a band a
///         later build records does not lose its figure to an earlier one running beside it in the same cache
///         home. There is no locking of its own, and none is needed: every read and write happens under the
///         solution's <see cref="JbRunLock" /> lease, which a caller must hold.
///     </para>
/// </remarks>
internal static class JbCostRecord
{
    private const string Extension = "cost";

    /// <summary>The subcommand whose figures are written under the band alone.</summary>
    private const string UnqualifiedSubcommand = "inspectcode";

    internal static string PathFor(string solutionPath, string cacheHome)
    {
        return JbSidecar.PathFor(solutionPath, cacheHome, Extension);
    }

    /// <summary>
    ///     Returns a band's one spelling, shared by the tokens in the file and the prose that quotes a figure,
    ///     so the two cannot come to disagree.
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
    ///     Records that a <paramref name="subcommand" /> run starting from <paramref name="band" /> took
    ///     <paramref name="cost" />, replacing whatever that pair last cost and leaving every other figure where
    ///     it was.
    /// </summary>
    /// <remarks>Whole seconds, the resolution a figure is rendered at.</remarks>
    internal static void Stamp(
        string solutionPath,
        string cacheHome,
        string subcommand,
        JbCostBand band,
        TimeSpan cost,
        ILogger logger)
    {
        try
        {
            string token = Token(subcommand, band);
            var seconds = (long)Math.Round(cost.TotalSeconds, MidpointRounding.AwayFromZero);

            List<string> lines = ExistingLines(PathFor(solutionPath, cacheHome));
            lines.RemoveAll(line => Records(line, token));
            lines.Add($"{token} {seconds.ToString(CultureInfo.InvariantCulture)}");

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
    ///     Returns what the last <paramref name="subcommand" /> run starting from <paramref name="band" /> cost, or
    ///     <see langword="null" /> when no figure this build can read is recorded for it.
    /// </summary>
    internal static TimeSpan? TryRead(string solutionPath, string cacheHome, string subcommand, JbCostBand band, ILogger logger)
    {
        try
        {
            string token = Token(subcommand, band);

            return ExistingLines(PathFor(solutionPath, cacheHome))
                .Where(line => Records(line, token))
                .Select(line => Seconds(line, token))
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
    internal static void Clear(string solutionPath, string cacheHome, ILogger logger)
    {
        JbSidecar.TryDelete(solutionPath, cacheHome, Extension, "recorded jb run costs", logger);
    }

    /// <summary>
    ///     Reads the record's lines untrimmed, because <see cref="Stamp" /> writes back every line it does not
    ///     recognise and must not rewrite another build's entries in passing.
    /// </summary>
    private static List<string> ExistingLines(string recordPath)
    {
        return JbSidecar.ReadLines(recordPath);
    }

    /// <summary>
    ///     Returns the token that opens the line recording <paramref name="subcommand" />'s figure for
    ///     <paramref name="band" />.
    /// </summary>
    private static string Token(string subcommand, JbCostBand band)
    {
        string label = Label(band);
        return subcommand == UnqualifiedSubcommand ? label : $"{subcommand} {label}";
    }

    /// <summary>Determines whether <paramref name="line" /> is the entry <paramref name="token" /> opens.</summary>
    private static bool Records(string line, string token)
    {
        return line.StartsWith(token + ' ', StringComparison.Ordinal);
    }

    /// <summary>
    ///     Returns the duration <paramref name="line" /> carries, or <see langword="null" /> when it carries
    ///     something this build cannot read as one.
    /// </summary>
    /// <remarks>
    ///     <see cref="NumberStyles.None" /> is the whole guard: it refuses a sign, a separator and surrounding
    ///     space, so a hand-edited or half-written file quotes nothing rather than quoting nonsense.
    /// </remarks>
    private static TimeSpan? Seconds(string line, string token)
    {
        string value = line[(token.Length + 1)..];

        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }
}