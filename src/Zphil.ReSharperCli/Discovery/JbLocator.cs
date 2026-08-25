using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Discovery;

/// <summary>A located <c>jb</c> executable and the version it reported.</summary>
internal sealed record JbInstallation(string ExecutablePath, string Version);

/// <summary>
///     Finds the <c>jb</c> (ReSharper CLI) executable by probing <c>jb inspectcode --version</c> against
///     each candidate location — PATH first, then the dotnet global-tools directory, which an MCP client
///     process may not inherit on PATH. The first success is cached; if every candidate fails, throws a
///     <see cref="UserErrorException" /> with install guidance.
/// </summary>
internal sealed class JbLocator(IProcessRunner processRunner, IEnvironment environment, ILogger<JbLocator> logger)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private JbInstallation? _cached;

    public async Task<JbInstallation> LocateAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null) return _cached;

        List<string> failures = [];
        foreach (string candidate in Candidates())
        {
            var elapsed = Stopwatch.StartNew();
            ProbeOutcome outcome = await ProbeAsync(candidate, cancellationToken);

            // Every candidate leaves a line whichever way it ended, because the case that costs the most is
            // the one nothing else records: a candidate that spends its whole thirty-second timeout, or
            // throws before ProcessRunner has written anything at all — the ordinary "not on PATH, fall
            // through to ~/.dotnet/tools" shape — and is then succeeded by a working one. That is time gone
            // before the call has done anything, and the "No jb found" line below is never reached to
            // account for it. The outcome clause is the point: ProcessRunner sees a spawn, this frame sees
            // the decision.
            logger.LogDebug(
                "Probed jb candidate {Candidate} in {ElapsedMs} ms — {ProbeOutcome}",
                candidate,
                elapsed.ElapsedMilliseconds,
                outcome.Detail);

            if (outcome.Version is null)
            {
                failures.Add($"  {candidate}: {outcome.Detail}");
                continue;
            }

            JbInstallation installation = new(candidate, outcome.Version);
            _cached = installation;
            return installation;
        }

        // Only successes are cached, so a machine with no jb re-probes every candidate on every call, and
        // pays the whole probe again each time.
        logger.LogDebug("No jb found. Tried:\n{Failures}", string.Join("\n", failures));

        throw new UserErrorException(NotFoundMessage(failures));
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
                ["inspectcode", "--version"],
                ProbeTimeout,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Missing executable (Win32Exception), a probe timeout, etc. — record and try the next.
            return new ProbeOutcome(null, exception.Message);
        }

        if (result.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"exited with code {result.ExitCode}"
                : result.StandardError.Trim();

            return new ProbeOutcome(null, detail);
        }

        string version = ParseVersion(result.StandardOutput);

        return string.IsNullOrWhiteSpace(version)
            ? new ProbeOutcome(null, "exited with code 0 but reported no version")
            : new ProbeOutcome(version, $"reported version {version}");
    }

    private IEnumerable<string> Candidates()
    {
        yield return "jb";

        string extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        yield return Path.Combine(environment.HomeDirectory, ".dotnet", "tools", $"jb{extension}");
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

    private static string NotFoundMessage(IEnumerable<string> failures)
    {
        return "JetBrains ReSharper CLI tools not found.\n\n"
               + "Tried:\n"
               + string.Join("\n", failures) + "\n\n"
               + "Install with:\n"
               + "  dotnet tool install JetBrains.ReSharper.GlobalTools -g\n\n"
               + "Then restart your terminal to update PATH.\n"
               + "Requires .NET SDK 8.0+ (https://dotnet.microsoft.com/download).";
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
    private sealed record ProbeOutcome(string? Version, string Detail);
}