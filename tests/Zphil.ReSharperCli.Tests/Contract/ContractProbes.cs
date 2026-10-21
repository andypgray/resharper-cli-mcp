using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Sarif;

namespace Zphil.ReSharperCli.Tests.Contract;

/// <summary>
///     Records what one subcommand's standard output looked like to <see cref="JbProgressLines" />: how much of
///     it was recognised, and which phases it announced.
/// </summary>
internal sealed class ProgressVocabulary
{
    private readonly HashSet<JbRunPhase> _phases = [];

    /// <summary>Gets how many non-blank lines the run wrote.</summary>
    public int Lines { get; private set; }

    /// <summary>Gets how many of those lines this server recognised.</summary>
    public int Recognised { get; private set; }

    /// <summary>Gets how many of the recognised lines name a file, which is what a heartbeat counts.</summary>
    public int FileLines { get; private set; }

    /// <summary>Gets the phases the run announced.</summary>
    public IReadOnlySet<JbRunPhase> Phases => _phases;

    public void Add(string line)
    {
        if (line.Trim().Length == 0) return;

        Lines++;

        if (JbProgressLines.Classify(line) is not { } step) return;

        Recognised++;
        _phases.Add(step.Phase);

        if (step.NamesAFile) FileLines++;
    }
}

/// <summary>Records how each subcommand answered an <c>--include</c> that was left absolute.</summary>
internal sealed record RawIncludeProbe(int CleanupExitCode, RawIncludeInspect Inspect);

/// <summary>Records what <c>inspectcode</c> did with an absolute <c>--include</c>.</summary>
/// <remarks>
///     It refuses the argument in a shape that varies by release: 2026.1 and earlier exit 0 with an empty
///     report, and 2026.2 exits non-zero without writing a report file. So the probe records the exit code and
///     the issue count rather than pinning either shape.
/// </remarks>
internal sealed record RawIncludeInspect(int ExitCode, int IssueCount);

/// <summary>
///     Records what a solution-wide inspect's report lists under SARIF's <c>run.artifacts</c>, against
///     <see cref="JbContractFixture.CleanFileName" />, a file the same run inspected and found nothing in.
/// </summary>
/// <remarks>
///     On <c>jb</c> 2026.2.3.1 the array holds one entry per file with a finding and leaves the clean file out,
///     so it cannot tell a <c>files</c> entry that matched nothing from a file that came out clean, and
///     <see cref="SarifParser" /> leaves it unread. A report that lists the clean file would make that
///     distinction possible, so the soft tier reports it as drift.
/// </remarks>
/// <param name="Unreadable">Why the report could not be read, or <see langword="null" /> when it was.</param>
/// <param name="ArtifactCount">
///     How many entries the <c>artifacts</c> arrays hold, or <see langword="null" /> when the report carries
///     none.
/// </param>
/// <param name="FilesWithFindings">How many distinct files the report's results name.</param>
/// <param name="CleanFileInspected">Whether jb's own output named the clean file as one it inspected.</param>
/// <param name="CleanFileFindings">How many findings the clean file drew.</param>
/// <param name="CleanFileListed">
///     Whether <c>artifacts</c> lists the clean file, or <see langword="null" /> when the report could not be
///     read.
/// </param>
internal sealed record SarifArtifactsProbe(
    string? Unreadable,
    int? ArtifactCount,
    int FilesWithFindings,
    bool CleanFileInspected,
    int CleanFileFindings,
    bool? CleanFileListed)
{
    /// <summary>What each part of <see cref="Signature" /> says, in its order, for whatever labels the row.</summary>
    internal const string SignatureColumns =
        "listed / files with findings; " + JbContractFixture.CleanFileName + " inspected / findings / listed";

    /// <summary>Gets the probe as one row of the soft report, in <see cref="SignatureColumns" /> order.</summary>
    /// <remarks>
    ///     Whether the clean file is listed answers something only when it was inspected and drew nothing.
    /// </remarks>
    public string Signature
    {
        get
        {
            string listed = Unreadable is not null ? "unreadable"
                : ArtifactCount is { } count ? $"{count}"
                : "no array";

            return $"{listed} / {FilesWithFindings}; {SoftReport.YesNo(CleanFileInspected)} / {CleanFileFindings} / "
                   + SoftReport.YesNo(CleanFileListed);
        }
    }
}