namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Where a <c>jb</c> run has got to, in the order a run passes through them.
/// </summary>
/// <remarks>
///     The first three are this server's own doing and are known without reading a line of <c>jb</c>'s
///     output; the last two are <c>jb</c>'s, and are reached only when it says so. That split is why
///     <see cref="Starting" /> exists as a phase of its own rather than being folded into
///     <see cref="Analyzing" />: <c>jb</c> spends its first half-minute loading the solution model and
///     saying nothing at all, and a run silently sitting in that prelude is exactly what a caller cannot
///     otherwise tell from a hang.
/// </remarks>
internal enum JbRunPhase
{
    /// <summary>Queueing for the cache generation's lease. No <c>jb</c> exists yet, and none can.</summary>
    Queued,

    /// <summary>Copying a sibling checkout's warm cache into this path's generation.</summary>
    Seeding,

    /// <summary><c>jb</c> is running but has reported nothing yet.</summary>
    Starting,

    /// <summary><c>inspectcode</c>'s analysis sweep — the long phase of a cold run.</summary>
    Analyzing,

    /// <summary><c>inspectcode</c>'s inspection sweep, which follows the analysis.</summary>
    Inspecting,

    /// <summary><c>cleanupcode</c> has finished analysing and is rewriting files.</summary>
    Cleaning
}

/// <summary>
///     What one line of <c>jb</c>'s standard output said about where the run has got to: the phase it puts
///     the run in, and whether the line named a file rather than announcing the phase.
/// </summary>
/// <param name="Phase">The phase the line places the run in.</param>
/// <param name="NamesAFile">
///     Whether this is one of the per-file lines, and so counts towards the "N files so far" a heartbeat
///     reports. False for a phase announcement, which resets that count instead.
/// </param>
internal readonly record struct JbProgressStep(JbRunPhase Phase, bool NamesAFile);

/// <summary>
///     Reads <c>jb</c>'s progress vocabulary off a single line of its standard output. Pure and stateless:
///     this is the piece the <c>JbContract</c> suite drives over the output of a real run, so what it knows
///     about someone else's tool is checked against that tool rather than assumed.
/// </summary>
/// <remarks>
///     <para>
///         The vocabulary, measured against <c>jb</c> 2026.2.1. <c>inspectcode</c> announces
///         <c>Analyzing files</c> and then prints one <c>Analyzing &lt;file&gt;</c> line per file, then
///         announces <c>Running inspections</c> and prints one <c>Inspecting &lt;file&gt;</c> line per file.
///         Cold on a large solution the analysis phase is over 90% of the run and the inspection phase is
///         seconds, which is why both are recognised but the analysis one is the one that matters.
///     </para>
///     <para>
///         <c>cleanupcode</c> shares none of it. It announces <c>Cleaning up using profile &lt;name&gt;</c>
///         and then prints one line per rewritten file — but those lines are bare
///         <c>&lt;project&gt;\&lt;path&gt;</c> strings with no prefix, which nothing here can tell from a
///         stray banner line. So cleanup gets the phase and no file count, and
///         <see cref="Services.JbRunner" /> never makes a count-based claim it cannot support.
///     </para>
///     <para>
///         Anything unrecognised is <see langword="null" />, which leaves the run in whatever phase it was
///         already in. A vocabulary change therefore costs a quieter notification and nothing else — the
///         reason this is watched by a soft-tier contract rather than pinned by a hard one.
///     </para>
/// </remarks>
internal static class JbProgressLines
{
    /// <summary><c>inspectcode</c>'s announcement that the analysis sweep is starting.</summary>
    internal const string AnalyzingPhaseLine = "Analyzing files";

    /// <summary><c>inspectcode</c>'s announcement that the inspection sweep is starting.</summary>
    internal const string InspectingPhaseLine = "Running inspections";

    /// <summary><c>cleanupcode</c>'s announcement that it is about to rewrite files.</summary>
    private const string CleaningPhasePrefix = "Cleaning up using profile";

    private const string AnalyzingFilePrefix = "Analyzing ";
    private const string InspectingFilePrefix = "Inspecting ";

    /// <summary>
    ///     What <paramref name="line" /> says about the run, or <see langword="null" /> when it says nothing
    ///     this server understands.
    /// </summary>
    /// <remarks>
    ///     The phase announcements are matched before the per-file prefixes they share an opening word with,
    ///     so <c>Analyzing files</c> is read as the announcement it is rather than as a file literally named
    ///     <c>files</c>.
    /// </remarks>
    internal static JbProgressStep? Classify(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.Length == 0) return null;

        if (trimmed == AnalyzingPhaseLine) return new JbProgressStep(JbRunPhase.Analyzing, false);

        if (trimmed == InspectingPhaseLine) return new JbProgressStep(JbRunPhase.Inspecting, false);

        if (trimmed.StartsWith(CleaningPhasePrefix, StringComparison.Ordinal))
            return new JbProgressStep(JbRunPhase.Cleaning, false);

        if (NamesAFileAfter(trimmed, AnalyzingFilePrefix)) return new JbProgressStep(JbRunPhase.Analyzing, true);

        if (NamesAFileAfter(trimmed, InspectingFilePrefix)) return new JbProgressStep(JbRunPhase.Inspecting, true);

        return null;
    }

    /// <summary>
    ///     Whether <paramref name="line" /> is <paramref name="prefix" /> followed by something. The
    ///     "followed by something" half is what stops a bare <c>Analyzing</c> — a truncated line, or a
    ///     future announcement that drops its noun — being counted as a file.
    /// </summary>
    private static bool NamesAFileAfter(string line, string prefix)
    {
        return line.StartsWith(prefix, StringComparison.Ordinal) && line.Length > prefix.Length;
    }
}