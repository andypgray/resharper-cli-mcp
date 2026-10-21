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
    ///     The severity a cache pre-warm reports at.
    /// </summary>
    /// <remarks>
    ///     The <c>resharper_inspect</c> default, not raised to shrink the discarded SARIF: <c>--severity</c> is
    ///     believed to be a report filter only, but were that belief wrong the warm-up would populate a cache
    ///     generation no real call opens and the feature would silently do nothing. An argument list identical
    ///     to the default real call's removes the question, and a test pins the two together.
    /// </remarks>
    internal const InspectSeverity WarmUpSeverity = InspectSeverity.Warning;

    /// <summary>What precedes the SARIF report's path on <c>jb</c>'s command line.</summary>
    internal const string OutputArgumentPrefix = "-o=";

    /// <summary>
    ///     Inspects <paramref name="config" />'s solution, optionally scoped to <paramref name="files" />, and
    ///     parses what <c>jb</c> reported at <paramref name="severity" /> or above.
    /// </summary>
    public async Task<IReadOnlyList<InspectIssue>> RunAsync(
        ResolvedConfig config,
        IReadOnlyList<string>? files,
        InspectSeverity severity,
        CancellationToken cancellationToken,
        Action<string>? onProgress = null)
    {
        return await WithSarifScratchAsync("resharper-inspect-", async outputFile =>
        {
            List<string> arguments = BuildArguments(config, outputFile, files, severity);

            ProcessResult result = await jbRunner.RunAsync(config, arguments, cancellationToken, onProgress);

            if (!File.Exists(outputFile))
                throw new UserErrorException(
                    $"jb inspectcode did not produce an output file.\n{JbRunner.StandardErrorTail(result.StandardError)}");

            try
            {
                await using FileStream sarif = File.OpenRead(outputFile);
                return (IReadOnlyList<InspectIssue>)await SarifParser.ParseAsync(sarif, cancellationToken);
            }
            catch (JsonException ex)
            {
                throw new UserErrorException(
                    $"jb inspectcode produced unparseable SARIF output: {ex.Message}", ex);
            }
        });
    }

    /// <summary>
    ///     Populates the solution's ReSharper cache generation speculatively, discarding the SARIF unread: the
    ///     run's value is in the cache it leaves behind.
    /// </summary>
    /// <remarks>
    ///     A warm-up is worth something only if it opens the same cache generation a real call will (same
    ///     <c>--caches-home</c>, <c>--settings</c>, <c>--swea</c> and extensions), so its arguments come from
    ///     <see cref="BuildArguments" />, whose order the tests pin, rather than from a second site that would
    ///     drift. <c>--include</c> does not shrink <c>jb</c>'s work, so this unscoped run warms what a scoped
    ///     call opens too.
    /// </remarks>
    public Task<SpeculativeRunOutcome> WarmCacheAsync(ResolvedConfig config, CancellationToken cancellationToken)
    {
        return WithSarifScratchAsync("resharper-warmup-", outputFile =>
        {
            List<string> arguments = BuildArguments(config, outputFile, null, WarmUpSeverity);

            return jbRunner.TryRunAsync(config, arguments, cancellationToken);
        });
    }

    /// <summary>Builds the <c>jb inspectcode</c> argument list. Order is pinned by tests.</summary>
    internal static List<string> BuildArguments(
        ResolvedConfig config,
        string outputFile,
        IReadOnlyList<string>? files,
        InspectSeverity severity)
    {
        List<string> arguments =
        [
            "inspectcode",
            config.SolutionPath,
            $"{OutputArgumentPrefix}{outputFile}",
            $"--severity={severity.ToJbToken()}",
            "--swea",
            "--no-build",
            "--absolute-paths"
        ];

        if (files is { Count: > 0 }) arguments.Add(JbRunner.IncludeArgument(files, config.SolutionDirectory));

        JbRunner.AppendConfigArguments(arguments, config);

        return arguments;
    }

    /// <summary>
    ///     Runs <paramref name="run" /> against a fresh scratch directory holding jb's <c>results.json</c>, and
    ///     deletes the directory best-effort when the run is over.
    /// </summary>
    /// <remarks>
    ///     One output-file lifecycle for every inspect run, so it cannot drift between a real call and a
    ///     warm-up. <paramref name="prefix" /> differs by kind of run: the path rides jb's command line, which is
    ///     how an operator tells a pre-warm process from a real one.
    /// </remarks>
    private static async Task<T> WithSarifScratchAsync<T>(string prefix, Func<string, Task<T>> run)
    {
        DirectoryInfo tempDirectory = Directory.CreateTempSubdirectory(prefix);
        try
        {
            string outputFile = Path.Combine(tempDirectory.FullName, "results.json");
            return await run(outputFile);
        }
        finally
        {
            TryDelete(tempDirectory);
        }
    }

    private static void TryDelete(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(true);
        }
        catch
        {
            // Best-effort: a leftover temp directory costs some disk, never the result already in hand.
        }
    }
}