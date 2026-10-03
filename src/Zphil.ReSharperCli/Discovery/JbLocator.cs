using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Discovery;

/// <summary>A located <c>jb</c> executable and the version it reported.</summary>
internal sealed record JbInstallation(string ExecutablePath, string Version);

/// <summary>
///     Finds the <c>jb</c> (ReSharper CLI) executable by probing <c>jb inspectcode --version</c> against
///     each candidate location — PATH first, then the dotnet global-tools directory, which an MCP client
///     process may not inherit on PATH.
/// </summary>
/// <remarks>
///     <para>
///         The first success is cached for as long as the files the candidates name are unchanged. If every
///         candidate fails, it throws a <see cref="UserErrorException" /> whose remedy is chosen from what the
///         probes proved — install guidance only when no candidate could be started at all.
///     </para>
///     <para>
///         The answer is re-checked by a stat on every call because the version outlives the build that
///         reported it. On a standard install both candidates reach the <c>dotnet tool</c> shim, which starts
///         whichever build is installed; the first candidate is whatever <c>jb</c> comes first on PATH,
///         usually but not always that shim. So after an in-place update every spawn runs the new build,
///         while a cached version would go on labelling its runs with the old one, and everything that records
///         which build warmed a cache reads that label. An in-place update rewrites the shim on Windows, so a
///         change in the write time or size of either candidate's file is what sends the next call to probe
///         again.
///     </para>
///     <para>
///         Not a probe per call: a probe costs seconds, and several times that with several servers starting
///         together, against a PATH lookup and a stat of one or two files.
///     </para>
///     <para>
///         Two gaps are accepted. The check runs when a call resolves its configuration, so a call that
///         resolved before <c>jb</c> was replaced and spawns it after, while queued behind another run say,
///         labels the new build's run with the old version. So does the pre-warm a timed-out call re-arms
///         with its own configuration. The next call sees the change, but reads the marker that run stamped
///         as another build's, so it reports the cache as stale and records its warm run under the cold band,
///         once. A second check once the run lock is held would close it.
///     </para>
///     <para>
///         The second gap is that the file stated is not always the file run. The stat covers what
///         <see cref="PathSearch" /> resolves, while the probe, and every spawn of
///         <see cref="JbInstallation.ExecutablePath" />, hand the bare name to the runtime's own lookup, and on
///         Windows that searches the application, working and system directories before PATH. A <c>jb</c>
///         found only there has nothing to compare and keeps its first answer. Probing and spawning the
///         resolved path instead would close the gap, but would stop finding a <c>jb</c> reachable only from
///         the working directory.
///     </para>
/// </remarks>
internal sealed class JbLocator(IProcessRunner processRunner, IEnvironment environment, ILogger<JbLocator> logger)
{
    /// <summary>
    ///     What a probe runs against a candidate. Internal, with <see cref="ProbeTimeout" /> and
    ///     <see cref="Candidates" />, because the contract suite's skip gate spawns the same probe under a
    ///     policy of its own — sharing the data is what keeps the gate finding every <c>jb</c> this class
    ///     would.
    /// </summary>
    internal static readonly string[] ProbeArguments = ["inspectcode", "--version"];

    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     The last successful probe and the candidate files it was taken against, swapped as one reference so
    ///     a reader never pairs one probe's answer with another moment's files.
    /// </summary>
    private Located? _located;

    public async Task<JbInstallation> LocateAsync(CancellationToken cancellationToken)
    {
        // Read before any probe, never after it: a jb replaced while a probe runs then no longer matches what
        // is recorded, and the next call probes again rather than trusting an answer older than the files.
        IReadOnlyList<JbFileState> files = ReadCandidateFiles();

        Located? located = _located;
        if (located is not null)
        {
            if (located.Files.SequenceEqual(files)) return located.Installation;

            logger.LogDebug(
                "jb's files have changed since it reported version {JbVersion}; probing again",
                located.Installation.Version);
        }

        List<string> failures = [];
        var evidence = ProbeEvidence.NotStarted;
        foreach (string candidate in Candidates(environment.HomeDirectory))
        {
            var elapsed = Stopwatch.StartNew();
            ProbeOutcome outcome = await ProbeAsync(candidate, cancellationToken);

            // Every candidate leaves a line whichever way it ended, because the case that costs the most is
            // the one nothing else records: a candidate that spends its whole thirty-second timeout, or
            // throws before ProcessRunner has written anything at all — the ordinary "not on PATH, fall
            // through to ~/.dotnet/tools" shape — and is then succeeded by a working one. That is time gone
            // before the call has done anything, and the "No jb reported a version" line below is never
            // reached to account for it. The outcome clause is the point: ProcessRunner sees a spawn,
            // this frame sees the decision.
            logger.LogDebug(
                "Probed jb candidate {Candidate} in {ElapsedMs} ms — {ProbeOutcome}",
                candidate,
                elapsed.ElapsedMilliseconds,
                outcome.Detail);

            if (outcome.Version is null)
            {
                failures.Add($"  {candidate}: {outcome.Detail}");

                // The most that any one candidate proved, across all of them: one that started is enough to
                // know a jb is there, however the others ended. A timeout does not end the search either: a
                // later candidate can still answer, and is often the same file by another route (the SDK puts
                // the global tools directory on PATH), so probing it is the retry the timeout's error would
                // otherwise send the caller to make.
                if (outcome.Evidence > evidence) evidence = outcome.Evidence;
                continue;
            }

            JbInstallation installation = new(candidate, outcome.Version);
            _located = new Located(installation, files);
            return installation;
        }

        // Only successes are cached, so a machine with no jb re-probes every candidate on every call, and
        // pays the whole probe again each time.
        logger.LogDebug("No jb reported a version. Tried:\n{Failures}", string.Join("\n", failures));

        throw new UserErrorException(FailureMessage(failures, evidence));
    }

    /// <summary>
    ///     Run <c>jb inspectcode --version</c> against one candidate and classify how it ended. Cancellation
    ///     by the caller's token is the one ending that is not an outcome — it means the whole call is going
    ///     away, so it propagates rather than being recorded as a candidate that failed.
    /// </summary>
    private async Task<ProbeOutcome> ProbeAsync(string candidate, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                candidate,
                ProbeArguments,
                ProbeTimeout,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProcessTimeoutException)
        {
            // Its own ending, because it is the one failure that proves the candidate is there: a process
            // has to start before it can be killed at the cap. The clause is written here rather than taken
            // from the exception, whose message names the executable a second time — redundant once it
            // follows the candidate.
            return new ProbeOutcome(
                null,
                $"timed out after {DurationFormatter.Format(ProbeTimeout)}",
                ProbeEvidence.TimedOut);
        }
        catch (Exception exception)
        {
            // A missing executable (Win32Exception), and anything else unclassified: nothing here proves
            // the candidate exists, which is what leaves install guidance as the remedy.
            return new ProbeOutcome(null, exception.Message);
        }

        if (result.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"exited with code {result.ExitCode}"
                : result.StandardError.Trim();

            return new ProbeOutcome(null, detail, ProbeEvidence.Ran);
        }

        string version = ParseVersion(result.StandardOutput);

        return string.IsNullOrWhiteSpace(version)
            ? new ProbeOutcome(null, "exited with code 0 but reported no version", ProbeEvidence.Ran)
            : new ProbeOutcome(version, $"reported version {version}", ProbeEvidence.Ran);
    }

    internal static IEnumerable<string> Candidates(string homeDirectory)
    {
        yield return "jb";

        string extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        yield return Path.Combine(homeDirectory, ".dotnet", "tools", $"jb{extension}");
    }

    /// <summary>
    ///     The write time and size of the file each candidate names right now, in candidate order.
    /// </summary>
    /// <remarks>
    ///     A candidate that names no file, or one that vanishes between the lookup and the stat, is left out,
    ///     so a <c>jb</c> uninstalled or installed since the last probe changes the list as surely as one
    ///     rewritten in place.
    /// </remarks>
    private List<JbFileState> ReadCandidateFiles()
    {
        string? pathVariable = environment.GetVariable(PathSearch.PathVariable);

        List<JbFileState> files = [];
        foreach (string candidate in Candidates(environment.HomeDirectory))
        {
            string? filePath = PathSearch.Resolve(candidate, pathVariable);
            if (filePath is null) continue;

            if (TryStat(filePath) is { } state) files.Add(state);
        }

        return files;
    }

    private static JbFileState? TryStat(string filePath)
    {
        try
        {
            FileInfo file = new(filePath);

            return file.Exists ? new JbFileState(filePath, file.LastWriteTimeUtc, file.Length) : null;
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            return null;
        }
    }

    /// <summary>
    ///     The version a probe reported, or an empty string when it reported nothing usable — including the
    ///     null a defaulted <see cref="ProcessResult" /> carries, which reaches here only through a test
    ///     double but must read as "no version" rather than crash the whole discovery path.
    /// </summary>
    private static string ParseVersion(string? standardOutput)
    {
        if (string.IsNullOrWhiteSpace(standardOutput)) return string.Empty;

        // Parse "Version: 2026.1.2" from the multi-line output; fall back to the trimmed whole output.
        foreach (string line in standardOutput.Split('\n'))
            if (line.StartsWith("Version:", StringComparison.Ordinal))
                return line["Version:".Length..].Trim();

        return standardOutput.Trim();
    }

    /// <summary>
    ///     The message for a call where no candidate reported a version, with the remedy chosen from what the
    ///     probes proved rather than from the bare fact that they all failed.
    /// </summary>
    /// <remarks>
    ///     Telling someone to install a tool they already have is worse than telling them nothing: it names a
    ///     remedy that cannot work, and it sends them to fix the wrong thing while the real cause — a machine
    ///     too busy to answer in half a minute — goes unnamed. Any candidate that <em>started</em> proves a
    ///     <c>jb</c> exists, so only <see cref="ProbeEvidence.NotStarted" /> keeps the install guidance. An
    ///     unrecognised value falls there too: the guidance that was always given is the safe thing to say
    ///     about a probe nothing has classified.
    /// </remarks>
    private static string FailureMessage(IEnumerable<string> failures, ProbeEvidence evidence)
    {
        string tried = "Tried:\n" + string.Join("\n", failures) + "\n\n";

        // The message for a jb that is there: what the strongest probe showed, then the remedy that follows.
        string Installed(string ending, string remedy)
        {
            return "JetBrains ReSharper CLI tools found, but no candidate reported a version.\n\n"
                   + tried
                   + $"At least one candidate started{ending}, "
                   + "so jb is installed and installing it again will not help.\n"
                   + remedy;
        }

        return evidence switch
        {
            ProbeEvidence.TimedOut => Installed(
                $" and was killed after {DurationFormatter.Format(ProbeTimeout)} without reporting one",
                "A probe that slow is usually a machine busy at startup rather than a broken install, so retry the call."),

            ProbeEvidence.Ran => Installed(
                "",
                "Run `jb inspectcode --version` yourself to see what it reports."),

            _ => "JetBrains ReSharper CLI tools not found.\n\n"
                 + tried
                 + "Install with:\n"
                 + "  dotnet tool install JetBrains.ReSharper.GlobalTools -g\n\n"
                 + "Then restart your terminal to update PATH.\n"
                 + "Requires .NET SDK 8.0+ (https://dotnet.microsoft.com/download)."
        };
    }

    /// <summary>
    ///     How one candidate's probe ended — the version it reported, or <see langword="null" /> with a clause
    ///     naming why it is not the <c>jb</c> to run.
    /// </summary>
    /// <remarks>
    ///     One clause serves both the per-candidate log line and the "Tried:" list in the user-facing error, so
    ///     what the log says about a candidate and what the error says about it cannot drift apart.
    /// </remarks>
    /// <param name="Version">The version parsed from the probe's output, or <see langword="null" /> on any failure.</param>
    /// <param name="Detail">What happened, phrased to follow the candidate's name.</param>
    /// <param name="Evidence">
    ///     What this probe proved about the candidate — which is what picks the remedy once every candidate
    ///     has failed. It defaults to <see cref="ProbeEvidence.NotStarted" /> so that a new failure path
    ///     added here claims nothing it has not shown.
    /// </param>
    private sealed record ProbeOutcome(string? Version, string Detail, ProbeEvidence Evidence = ProbeEvidence.NotStarted);

    /// <summary>A successful probe and the candidate files as they stood when it began.</summary>
    private sealed record Located(JbInstallation Installation, IReadOnlyList<JbFileState> Files);

    /// <summary>
    ///     One candidate's file as a stat sees it. The size alone would not do, because an in-place update
    ///     can leave it as it was: the shim is an apphost whose size does not depend on the build it starts.
    /// </summary>
    private readonly record struct JbFileState(string FilePath, DateTime LastWriteTimeUtc, long Length);

    /// <summary>
    ///     What a probe proved about a candidate, ordered by how much of it: nothing, that it runs, that it
    ///     runs and had not finished at the cap.
    /// </summary>
    /// <remarks>
    ///     The numeric order is the rank <see cref="LocateAsync" /> keeps the maximum of, so a new member goes
    ///     in its place in that order rather than on the end. <see cref="TimedOut" /> outranks
    ///     <see cref="Ran" /> because it is the one with a remedy of its own: waiting, rather than reading
    ///     what the candidate printed.
    /// </remarks>
    private enum ProbeEvidence
    {
        /// <summary>The candidate could not be started, so nothing about it says a <c>jb</c> is installed.</summary>
        NotStarted = 0,

        /// <summary>The candidate ran to an exit, whether or not it reported a version — so one is installed.</summary>
        Ran = 1,

        /// <summary>The candidate was still running at <see cref="ProbeTimeout" /> — one is installed, and was slow.</summary>
        TimedOut = 2
    }
}