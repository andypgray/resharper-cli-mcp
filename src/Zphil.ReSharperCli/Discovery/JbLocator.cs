using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Discovery;

/// <summary>Represents a located <c>jb</c> executable and the version it reported.</summary>
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
///         probes proved.
///     </para>
///     <para>
///         The answer is re-checked by a stat on every call, because the version outlives the build that
///         reported it. On a standard install both candidates reach the <c>dotnet tool</c> shim, which starts
///         whichever build is installed (the first candidate is whatever <c>jb</c> comes first on PATH, usually
///         but not always that shim). So after an in-place update every spawn runs the new build, while a
///         cached version would go on labelling its runs, and the caches they warm, with the old one. An
///         in-place update rewrites the shim on Windows, so a change in the write time or size of either
///         candidate's file sends the next call to probe again. A stat rather than a probe per call, because a
///         probe costs seconds, more with several servers starting together.
///     </para>
///     <para>
///         The check runs when a call resolves its configuration, so a call that resolved before <c>jb</c> was
///         replaced and spawns it after, such as one queued behind another run or the pre-warm a timed-out call
///         re-arms, labels the new build's run with the old version. The next call reads the marker that run
///         stamped as another build's, so it reports the cache as stale and records its warm run under the cold
///         band, once.
///     </para>
///     <para>
///         The file stated is not always the file run either. The stat covers what <see cref="PathSearch" />
///         resolves, while the probe, and every spawn of <see cref="JbInstallation.ExecutablePath" />, hand the
///         bare name to the runtime's own lookup, which on Windows searches the application, working and system
///         directories before PATH. A <c>jb</c> found only there has nothing to compare and keeps its first
///         answer.
///     </para>
/// </remarks>
internal sealed class JbLocator(IProcessRunner processRunner, IEnvironment environment, ILogger<JbLocator> logger)
{
    /// <summary>
    ///     What a probe runs against a candidate. Shared, with <see cref="ProbeTimeout" /> and
    ///     <see cref="Candidates" />, so anything else that probes for <c>jb</c> finds every <c>jb</c> this
    ///     class would.
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

            // Every candidate leaves a line whichever way it ended, because a candidate that spends its whole
            // timeout, or throws before ProcessRunner has written anything, and is then succeeded by a working
            // one is otherwise recorded nowhere: the failure line below is never reached. ProcessRunner sees a
            // spawn; only this frame sees the decision.
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
    ///     Runs <c>jb inspectcode --version</c> against one candidate and classifies how it ended.
    /// </summary>
    /// <remarks>
    ///     Cancellation by the caller's token is the one ending that is not an outcome — it means the whole
    ///     call is going away, so it propagates rather than being recorded as a candidate that failed.
    /// </remarks>
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
            // from the exception, whose message names the executable a second time.
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
    ///     Reads the write time and size of the file each candidate names right now, in candidate order.
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

    /// <summary>Parses the version a probe reported.</summary>
    /// <remarks>
    ///     Returns the empty string when it reported nothing usable, including the null a defaulted
    ///     <see cref="ProcessResult" /> carries, which must read as "no version" rather than crash the whole
    ///     discovery path.
    /// </remarks>
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
    ///     Builds the message for a call where no candidate reported a version, with the remedy chosen from what
    ///     the probes proved rather than from the bare fact that they all failed.
    /// </summary>
    /// <remarks>
    ///     Telling someone to install a tool they already have is worse than telling them nothing: it names a
    ///     remedy that cannot work and leaves the cause, such as a machine too busy to answer within
    ///     <see cref="ProbeTimeout" />, unnamed. Any candidate that started proves a <c>jb</c> exists, so
    ///     only <see cref="ProbeEvidence.NotStarted" /> keeps the install guidance. An unrecognised value falls
    ///     there too, the safe thing to say about a probe nothing has classified.
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
    ///     Represents how one candidate's probe ended: the version it reported, or <see langword="null" /> with
    ///     a clause naming why it is not the <c>jb</c> to run.
    /// </summary>
    /// <remarks>
    ///     One clause serves both the per-candidate log line and the "Tried:" list in the user-facing error, so
    ///     what the log says about a candidate and what the error says about it cannot drift apart.
    /// </remarks>
    /// <param name="Version">The version parsed from the probe's output, or <see langword="null" /> on any failure.</param>
    /// <param name="Detail">What happened, phrased to follow the candidate's name.</param>
    /// <param name="Evidence">
    ///     What this probe proved about the candidate. It defaults to <see cref="ProbeEvidence.NotStarted" /> so
    ///     that a new failure path claims nothing it has not shown.
    /// </param>
    private sealed record ProbeOutcome(string? Version, string Detail, ProbeEvidence Evidence = ProbeEvidence.NotStarted);

    /// <summary>Represents a successful probe and the candidate files as they stood when it began.</summary>
    private sealed record Located(JbInstallation Installation, IReadOnlyList<JbFileState> Files);

    /// <summary>
    ///     Represents one candidate's file as a stat sees it.
    /// </summary>
    /// <remarks>
    ///     The write time as well as the size, because an in-place update can leave the size as it was: the
    ///     shim is an apphost whose size does not depend on the build it starts.
    /// </remarks>
    private readonly record struct JbFileState(string FilePath, DateTime LastWriteTimeUtc, long Length);

    /// <summary>
    ///     Represents what a probe proved about a candidate, ordered by how much: nothing, that it runs, that it
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