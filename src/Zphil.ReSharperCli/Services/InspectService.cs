using System.Text.Json;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Sarif;

namespace Zphil.ReSharperCli.Services;

/// <summary>
///     Runs <c>jb inspectcode</c> into a throwaway temp directory, then parses its SARIF into
///     <see cref="InspectIssue" /> records. Read-only: it never touches the user's files.
/// </summary>
internal sealed class InspectService(JbRunner jbRunner)
{
    /// <summary>
    ///     The severity a cache pre-warm reports at. Matching the <c>resharper_inspect</c> default rather
    ///     than raising it to shrink the discarded SARIF is deliberate: <c>--severity</c> is believed to be a
    ///     report filter only, but if that belief is ever wrong the warm-up would populate a cache generation
    ///     no real call opens and the whole feature would silently do nothing, with no signal. An identical
    ///     argument list to the commonest real call removes the question, and a test pins the two together.
    /// </summary>
    internal const string WarmUpSeverity = "WARNING";

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

            ProcessResult result = await jbRunner.RunAsync(config, arguments, cancellationToken);

            if (!File.Exists(outputFile))
                throw new UserErrorException(
                    $"jb inspectcode did not produce an output file.\n{JbRunner.StandardErrorTail(result.StandardError)}");

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

    /// <summary>
    ///     Populate the solution's ReSharper cache generation speculatively, discarding the SARIF unread —
    ///     the run's value is entirely in the cache it leaves behind. Returns <see langword="null" /> when the
    ///     run did not happen or was handed back to a real call; a non-zero exit is reported rather than
    ///     thrown, because there is no user waiting on this.
    /// </summary>
    /// <remarks>
    ///     Lives here rather than in <see cref="CacheWarmer" /> because a warm-up is only worth anything if it
    ///     opens the <em>same</em> cache generation a real call will — same <c>--caches-home</c>,
    ///     <c>--settings</c>, <c>--swea</c>, extensions and all — and a second argument-building site would
    ///     drift from <see cref="BuildArguments" /> with only one of the two covered by the pinned-order
    ///     tests. So this class owns <em>how</em> a warm-up runs and <see cref="CacheWarmer" /> owns
    ///     <em>when</em>; the warmer never sees a jb argument. Note that <c>--include</c> does not shrink jb's
    ///     work, so a warm-up is inherently a full-solution run.
    /// </remarks>
    public async Task<ProcessResult?> WarmCacheAsync(ResolvedConfig config, CancellationToken cancellationToken)
    {
        DirectoryInfo tempDirectory = Directory.CreateTempSubdirectory("resharper-warmup-");
        try
        {
            string outputFile = Path.Combine(tempDirectory.FullName, "results.json");
            var arguments = BuildArguments(config, outputFile, null, WarmUpSeverity);

            return await jbRunner.TryRunAsync(config, arguments, cancellationToken);
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