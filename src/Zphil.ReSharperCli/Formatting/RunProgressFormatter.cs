using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     Renders one <see cref="JbRunProgressSnapshot" /> as the single line an MCP progress notification
///     carries. Pure, like every other formatter here: <see cref="JbRunProgress" /> owns the state and the
///     timer, and this owns nothing.
/// </summary>
/// <remarks>
///     <para>
///         Naming the run cap is the deliberate part. A caller watching "8 minutes of a 10 minute cap" can
///         raise <c>RESHARPER_MCP_TIMEOUT_SECS</c> before the failure instead of learning the cap exists from
///         the failure — which is the only way that variable has ever been discovered. The cap appears only
///         once it is armed, because queue time is outside the run budget and a message that charged the wait
///         against the cap would send a caller to raise the one number that was not the problem.
///     </para>
///     <para>
///         There is no <c>total</c> anywhere, and its absence is a choice rather than a gap.
///         <c>jb</c> announces no file count up front and its two sweeps disagree about how many files a
///         solution has, so a denominator would have to be invented. Elapsed-against-cap is the one honest
///         denominator available and it is the wrong one to draw: a client renders <c>total</c> as a filling
///         bar, which reads as "work done" while meaning "budget consumed" — worse than no bar at all.
///     </para>
/// </remarks>
internal static class RunProgressFormatter
{
    public static string Format(JbRunProgressSnapshot state)
    {
        var prefix = $"{state.Subcommand} on {Path.GetFileName(state.SolutionPath)}: ";

        // The first heartbeat is immediate and lands while the run is still nominally queued; at that
        // instant "waiting for another run" would be a claim about another session that nothing has
        // established. The snapshot owns the judgement — see JbRunProgressSnapshot.JustArrived.
        if (state.JustArrived) return prefix + "starting";

        string clause = Clause(state);
        string duration = Duration(state);

        return $"{prefix}{clause} — {duration}";
    }

    /// <summary>What the run is doing, in the caller's terms rather than <c>jb</c>'s.</summary>
    private static string Clause(JbRunProgressSnapshot state)
    {
        return state.Phase switch
        {
            JbRunPhase.Queued => "waiting for another run on this solution's ReSharper cache",
            JbRunPhase.Seeding => "copying a sibling checkout's warm cache",

            // The cache state alone, which is what a caller most wants during this phase: jb spends its first
            // half-minute loading the solution model without a word, and how that silence is about to end is
            // predicted by the cache far better than by anything jb has said (which is nothing). Never null
            // here — Starting is only ever entered through Spawning, whose summary is required.
            JbRunPhase.Starting => state.CacheSummary!,
            JbRunPhase.Analyzing => $"analyzing {Files(state.FilesSeen)}",
            JbRunPhase.Inspecting => $"inspecting {Files(state.FilesSeen)}",

            // No count: cleanupcode's per-file lines are bare paths that nothing can tell from a banner line,
            // so the phase is reported and the number is not invented.
            JbRunPhase.Cleaning => "rewriting files",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state.Phase, "Unmapped jb run phase.")
        };
    }

    /// <summary>
    ///     How long, and — once <c>jb</c> is running — against what. <see cref="DurationFormatter" /> rather
    ///     than a second spelling, so a cap someone set to 90 seconds reads the same here as in the message
    ///     they get if it bites.
    /// </summary>
    /// <remarks>
    ///     The cap is appended as its own clause rather than folded in as "of a 10 minute cap", because
    ///     <see cref="DurationFormatter" /> pluralizes — a shared spelling is worth more than the
    ///     attributive reading, and "of a 10 minutes cap" is the alternative.
    /// </remarks>
    private static string Duration(JbRunProgressSnapshot state)
    {
        string elapsed = DurationFormatter.Format(state.Elapsed);

        return state.Cap is { } cap ? $"{elapsed}, cap {DurationFormatter.Format(cap)}" : elapsed;
    }

    /// <summary>
    ///     The file count, or the bare noun before <c>jb</c> has named one. A phase announcement arrives
    ///     ahead of the first file line, so "analyzing 0 files" is reachable and says less than the plain
    ///     phrase does. Internal because the timeout message restates the same count — one spelling, so a
    ///     caller who watched "analyzing 40 files" for minutes is not told "40 file(s)" when it fails.
    /// </summary>
    internal static string Files(int count)
    {
        return count switch
        {
            0 => "files",
            1 => "1 file",
            _ => $"{count} files"
        };
    }
}