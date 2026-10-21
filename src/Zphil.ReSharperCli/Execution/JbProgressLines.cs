namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Defines where a <c>jb</c> run has got to, in the order a run passes through the phases.
/// </summary>
/// <remarks>
///     <see cref="Turn" />, <see cref="Queued" />, <see cref="Seeding" /> and <see cref="Starting" /> are this
///     server's own doing, known without reading a line of <c>jb</c>'s output; the rest are reached only when
///     <c>jb</c> says so. <see cref="Starting" /> is a phase of its own rather than part of
///     <see cref="Analyzing" /> because <c>jb</c> loads the solution model in silence before its first progress
///     line, and a run sitting in that prelude is otherwise indistinguishable from a hang.
/// </remarks>
internal enum JbRunPhase
{
    /// <summary>
    ///     Waiting for this server's slot: another run of this server holds it, whichever solution that run
    ///     is on.
    /// </summary>
    Turn,

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
///     Represents what one line of <c>jb</c>'s standard output said about where the run has got to: the phase it
///     puts the run in, and whether the line named a file rather than announcing the phase.
/// </summary>
internal readonly record struct JbProgressStep(JbRunPhase Phase, bool NamesAFile);

/// <summary>
///     Reads <c>jb</c>'s progress vocabulary off a single line of its standard output.
/// </summary>
/// <remarks>
///     <para>
///         Pure and stateless, so what it assumes about <c>jb</c>'s output can be checked against the output
///         of a real run.
///     </para>
///     <para>
///         The vocabulary, as observed on <c>jb</c> 2026.2.1. <c>inspectcode</c> announces
///         <c>Analyzing files</c> and then prints one <c>Analyzing &lt;file&gt;</c> line per file, then
///         announces <c>Running inspections</c> and prints one <c>Inspecting &lt;file&gt;</c> line per file.
///         Cold on a large solution the analysis phase is nearly the whole run and the inspection phase takes
///         seconds, which is why both are recognised but the analysis one is the one that matters.
///     </para>
///     <para>
///         <c>cleanupcode</c> shares none of it. It announces <c>Cleaning up using profile &lt;name&gt;</c> and
///         then prints one line per rewritten file, but those lines are bare <c>&lt;project&gt;\&lt;path&gt;</c>
///         strings with no prefix, which nothing here can tell from a stray banner line. So cleanup gets the
///         phase and no file count.
///     </para>
///     <para>
///         Anything unrecognised is <see langword="null" />, so a change in <c>jb</c>'s vocabulary costs quieter
///         progress and nothing else.
///     </para>
/// </remarks>
internal static class JbProgressLines
{
    /// <summary><c>inspectcode</c>'s announcement that the analysis sweep is starting.</summary>
    internal const string AnalyzingPhaseLine = "Analyzing files";

    /// <summary><c>inspectcode</c>'s announcement that the inspection sweep is starting.</summary>
    internal const string InspectingPhaseLine = "Running inspections";

    /// <summary><c>cleanupcode</c>'s announcement that it is about to rewrite files, which the profile name follows.</summary>
    internal const string CleaningPhasePrefix = "Cleaning up using profile";

    /// <summary>What precedes the file name on <c>inspectcode</c>'s per-file line during the analysis sweep.</summary>
    internal const string AnalyzingFilePrefix = "Analyzing ";

    /// <summary>What precedes the file name on <c>inspectcode</c>'s per-file line during the inspection sweep.</summary>
    internal const string InspectingFilePrefix = "Inspecting ";

    /// <summary>
    ///     Returns what <paramref name="line" /> says about the run, or <see langword="null" /> when it says
    ///     nothing this server understands.
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
    ///     Determines whether <paramref name="line" /> is <paramref name="prefix" /> followed by something.
    /// </summary>
    /// <remarks>
    ///     The "followed by something" half stops a bare <c>Analyzing</c>, such as a truncated line or a future
    ///     announcement that drops its noun, being counted as a file.
    /// </remarks>
    private static bool NamesAFileAfter(string line, string prefix)
    {
        return line.StartsWith(prefix, StringComparison.Ordinal) && line.Length > prefix.Length;
    }
}