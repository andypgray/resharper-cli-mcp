using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Services;

/// <summary>
///     The one path by which a <c>jb</c> subcommand is run: it takes the cross-process
///     <see cref="JbRunLock" /> for the solution's cache generation, spawns <c>jb</c> under the run
///     timeout, and turns a non-zero exit into a <see cref="UserErrorException" /> quoting the tail of
///     standard error. Inspect and cleanup share one cache generation, so the lock has to be taken in one
///     place rather than at both call sites by convention.
/// </summary>
/// <remarks>
///     Queue time is deliberately outside the run budget: the timeout below is armed inside
///     <see cref="ProcessRunner" />, which starts only once the lock is held, so a call that waited for
///     another run still gets its own full budget.
/// </remarks>
internal sealed class JbRunner(IProcessRunner processRunner, JbRunLock runLock)
{
    private const int StandardErrorTailLength = 2000;

    /// <summary>Wall-clock cap on one <c>jb</c> run, after which its process tree is killed.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Run <c>jb</c> with <paramref name="arguments" /> — whose first entry is the subcommand — against
    ///     the solution in <paramref name="config" />, returning its result for the caller's own
    ///     post-checks.
    /// </summary>
    public async Task<ProcessResult> RunAsync(
        ResolvedConfig config,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using IDisposable runLease = await runLock.AcquireAsync(config.SolutionPath, config.CacheHome, cancellationToken);

        ProcessResult result = await processRunner.RunAsync(
            config.JbExecutablePath, arguments, Timeout, cancellationToken);

        if (result.ExitCode != 0)
            throw new UserErrorException(
                $"jb {arguments[0]} exited with code {result.ExitCode}.\n{StandardErrorTail(result.StandardError)}");

        return result;
    }

    /// <summary>
    ///     The last <see cref="StandardErrorTailLength" /> characters of <paramref name="standardError" />,
    ///     trailing whitespace trimmed — enough of a failed run's output to diagnose it without flooding
    ///     the response.
    /// </summary>
    internal static string StandardErrorTail(string standardError)
    {
        string trimmed = standardError.TrimEnd();
        return trimmed.Length <= StandardErrorTailLength ? trimmed : trimmed[^StandardErrorTailLength..];
    }
}