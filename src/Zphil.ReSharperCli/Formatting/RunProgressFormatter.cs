using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     Renders one <see cref="JbRunProgressSnapshot" /> as the single line an MCP progress notification
///     carries.
/// </summary>
/// <remarks>
///     It names the run cap, so a caller watching a run near it can raise <c>RESHARPER_MCP_TIMEOUT_SECS</c>
///     before the failure instead of learning from the failure that the cap exists. The cap appears only once it
///     is armed, because queue time is outside the run budget, and charging the wait against the cap would send a
///     caller to raise the one number that was not the problem.
/// </remarks>
internal static class RunProgressFormatter
{
    public static string Format(JbRunProgressSnapshot state)
    {
        var prefix = $"{state.Subcommand} on {Path.GetFileName(state.SolutionPath)}: ";

        // The first heartbeat is immediate and lands while the run is still nominally waiting, when "waiting
        // for another run" would claim a run nothing has established. JbRunProgressSnapshot.JustArrived owns
        // that judgement.
        if (state.JustArrived) return prefix + "starting";

        string clause = Clause(state);
        string duration = Duration(state);

        return $"{prefix}{clause} — {duration}";
    }

    /// <summary>Describes what the run is doing, in the caller's terms rather than <c>jb</c>'s.</summary>
    private static string Clause(JbRunProgressSnapshot state)
    {
        return state.Phase switch
        {
            // No cap, because nothing is armed yet, and no holder either: the run ahead can change while
            // this caller waits, so only the log line, written once the wait is served, names it.
            JbRunPhase.Turn => "waiting for this server's other jb run to finish (it runs one at a time)",
            JbRunPhase.Queued => "waiting for another run on this solution's ReSharper cache",
            JbRunPhase.Seeding => "copying a sibling checkout's warm cache",

            // The cache state alone: jb loads the solution model without a word before its first progress
            // line, and the cache state is what predicts how long that silence lasts. Never null here:
            // Starting is entered only through Spawning, whose summary is required.
            JbRunPhase.Starting => state.CacheSummary!,
            JbRunPhase.Analyzing => $"analyzing {Files(state.FilesSeen)}",
            JbRunPhase.Inspecting => $"inspecting {Files(state.FilesSeen)}",

            // No count: cleanupcode's per-file lines are bare paths that nothing can tell from a banner line.
            JbRunPhase.Cleaning => "rewriting files",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state.Phase, "Unmapped jb run phase.")
        };
    }

    /// <summary>
    ///     Formats how long the run has taken and, once <c>jb</c> is running, the cap it runs against.
    /// </summary>
    /// <remarks>
    ///     The cap is its own clause rather than "of a 10 minute cap", because <see cref="DurationFormatter" />
    ///     pluralizes and would write "of a 10 minutes cap".
    /// </remarks>
    private static string Duration(JbRunProgressSnapshot state)
    {
        string elapsed = DurationFormatter.Format(state.Elapsed);

        return state.Cap is { } cap ? $"{elapsed}, cap {DurationFormatter.Format(cap)}" : elapsed;
    }

    /// <summary>
    ///     Formats the file count, or the bare noun before <c>jb</c> has named one.
    /// </summary>
    /// <remarks>
    ///     A phase announcement arrives ahead of the first file line, so "analyzing 0 files" is reachable and
    ///     says less than the plain phrase does. Shared so the count reads the same wherever it is restated: a
    ///     caller who watched "analyzing 40 files" for minutes is not then told "40 file(s)" when the run fails.
    /// </remarks>
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