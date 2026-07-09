namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     The outcome of running an external process to completion: its exit code and the full
///     (10&#160;MB-capped) text captured from standard output and standard error.
/// </summary>
internal readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
///     The single seam through which all product code spawns external processes (i.e. <c>jb</c>).
///     Faked with NSubstitute in tests so no test launches a real process except
///     <c>ProcessRunnerTests</c>.
/// </summary>
internal interface IProcessRunner
{
    /// <summary>
    ///     Run <paramref name="fileName" /> with <paramref name="arguments" /> (passed verbatim, never
    ///     shell-joined), capturing stdout/stderr. A non-zero exit code is <em>returned</em> in the
    ///     result, not thrown. Exceeding <paramref name="timeout" /> kills the process tree and throws
    ///     <see cref="UserErrorException" />; a missing executable surfaces as a
    ///     <see cref="System.ComponentModel.Win32Exception" />.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}