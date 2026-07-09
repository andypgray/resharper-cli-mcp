using System.Text.Json;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Sarif;

namespace Zphil.ReSharperCli.Services;

/// <summary>
///     Runs <c>jb inspectcode</c> into a throwaway temp directory, then parses its SARIF into
///     <see cref="InspectIssue" /> records. Read-only: it never touches the user's files.
/// </summary>
internal sealed class InspectService(IProcessRunner processRunner)
{
    private const int StandardErrorTailLength = 2000;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public async Task<List<InspectIssue>> RunAsync(
        ResolvedConfig config,
        IReadOnlyList<string>? files,
        string severity,
        CancellationToken cancellationToken)
    {
        DirectoryInfo tempDirectory = Directory.CreateTempSubdirectory("resharper-inspect-");
        try
        {
            string outputFile = Path.Combine(tempDirectory.FullName, "results.json");
            var arguments = BuildArguments(config, outputFile, files, severity);

            ProcessResult result = await processRunner.RunAsync(
                config.JbExecutablePath, arguments, Timeout, cancellationToken);

            if (result.ExitCode != 0)
                throw new UserErrorException(
                    $"jb inspectcode exited with code {result.ExitCode}.\n{StandardErrorTail(result.StandardError)}");

            if (!File.Exists(outputFile))
                throw new UserErrorException(
                    $"jb inspectcode did not produce an output file.\n{StandardErrorTail(result.StandardError)}");

            string content = await File.ReadAllTextAsync(outputFile, cancellationToken);
            try
            {
                return SarifParser.Parse(content);
            }
            catch (JsonException ex)
            {
                throw new UserErrorException(
                    $"jb inspectcode produced unparseable SARIF output: {ex.Message}", ex);
            }
        }
        finally
        {
            TryDelete(tempDirectory);
        }
    }

    /// <summary>Build the <c>jb inspectcode</c> argument list. Order is pinned by tests.</summary>
    internal static List<string> BuildArguments(
        ResolvedConfig config,
        string outputFile,
        IReadOnlyList<string>? files,
        string severity)
    {
        List<string> arguments =
        [
            "inspectcode",
            config.SolutionPath,
            $"-o={outputFile}",
            $"--severity={severity}",
            "--swea",
            "--no-build",
            "--absolute-paths",
            $"--caches-home={config.CacheHome}"
        ];

        if (config.SettingsPath is not null) arguments.Add($"--settings={config.SettingsPath}");

        if (files is { Count: > 0 }) arguments.Add($"--include={string.Join(";", files)}");

        if (!string.IsNullOrEmpty(config.Extensions)) arguments.Add($"-x={config.Extensions}");

        if (!string.IsNullOrEmpty(config.ExtensionSource)) arguments.Add($"--source={config.ExtensionSource}");

        return arguments;
    }

    private static string StandardErrorTail(string standardError)
    {
        string trimmed = standardError.TrimEnd();
        return trimmed.Length <= StandardErrorTailLength ? trimmed : trimmed[^StandardErrorTailLength..];
    }

    private static void TryDelete(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(true);
        }
        catch
        {
            // Best-effort cleanup of the temp results directory.
        }
    }
}