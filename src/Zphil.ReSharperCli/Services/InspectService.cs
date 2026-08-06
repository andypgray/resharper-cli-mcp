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
    internal const InspectSeverity WarmUpSeverity = InspectSeverity.Warning;

    public async Task<IReadOnlyList<InspectIssue>> RunAsync(
        ResolvedConfig config,
        IReadOnlyList<string>? files,
        InspectSeverity severity,
        CancellationToken cancellationToken)
    {
        return await WithSarifScratchAsync("resharper-inspect-", async outputFile =>
        {
            var arguments = BuildArguments(config, outputFile, files, severity);

            ProcessResult result = await jbRunner.RunAsync(config, arguments, cancellationToken);

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
    public Task<ProcessResult?> WarmCacheAsync(ResolvedConfig config, CancellationToken cancellationToken)
    {
        return WithSarifScratchAsync("resharper-warmup-", outputFile =>
        {
            var arguments = BuildArguments(config, outputFile, null, WarmUpSeverity);

            return jbRunner.TryRunAsync(config, arguments, cancellationToken);
        });
    }

    /// <summary>Build the <c>jb inspectcode</c> argument list. Order is pinned by tests.</summary>
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
            $"-o={outputFile}",
            $"--severity={severity.ToString().ToUpperInvariant()}",
            "--swea",
            "--no-build",
            "--absolute-paths"
        ];

        if (files is { Count: > 0 }) arguments.Add(JbRunner.IncludeArgument(files));

        JbRunner.AppendConfigArguments(arguments, config);

        return arguments;
    }

    /// <summary>
    ///     One scratch directory per run, holding jb's <c>results.json</c>, deleted best-effort when the run
    ///     is over. Shared by the real call and the warm-up so the output-file lifecycle cannot drift between
    ///     them. The <paramref name="prefix" /> stays distinct per caller: the path rides jb's command line,
    ///     which is how an operator tells a pre-warm process from a real one.
    /// </summary>
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
            // Best-effort cleanup of the temp results directory.
        }
    }
}