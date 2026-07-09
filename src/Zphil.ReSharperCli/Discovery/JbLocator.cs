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
internal sealed class JbLocator(IProcessRunner processRunner, IEnvironment environment)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private JbInstallation? _cached;

    public async Task<JbInstallation> LocateAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null) return _cached;

        List<string> failures = [];
        foreach (string candidate in Candidates())
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
                failures.Add($"  {candidate}: {exception.Message}");
                continue;
            }

            if (result.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(result.StandardError)
                    ? $"exited with code {result.ExitCode}"
                    : result.StandardError.Trim();
                failures.Add($"  {candidate}: {detail}");
                continue;
            }

            JbInstallation installation = new(candidate, ParseVersion(result.StandardOutput));
            _cached = installation;
            return installation;
        }

        throw new UserErrorException(NotFoundMessage(failures));
    }

    private IEnumerable<string> Candidates()
    {
        yield return "jb";

        string extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        yield return Path.Combine(environment.HomeDirectory, ".dotnet", "tools", $"jb{extension}");
    }

    private static string ParseVersion(string standardOutput)
    {
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
}