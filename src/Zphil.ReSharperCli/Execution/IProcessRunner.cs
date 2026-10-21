namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Represents the outcome of running an external process to completion: its exit code and the text
///     captured from standard output and standard error, each up to the runner's capture cap.
/// </summary>
/// <remarks>
///     Within that cap the text is complete, unless a process the child started still held a pipe open at the
///     timeout, in which case it is what had been read by then.
/// </remarks>
internal readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
///     The exception that is thrown when a process outlives the timeout its caller chose and has its tree
///     killed.
/// </summary>
/// <remarks>
///     A <see cref="UserErrorException" /> so that an escape still reads as an expected failure rather than
///     a logged crash, and a distinct type so the caller that set the timeout can answer for it: only that
///     caller knows whether the cap can be raised, and by what. The message here states the mechanical fact
///     and nothing more.
/// </remarks>
internal sealed class ProcessTimeoutException(string message) : UserErrorException(message);

/// <summary>
///     Defines the seam through which product code runs <c>jb</c>.
/// </summary>
internal interface IProcessRunner
{
    /// <summary>
    ///     Runs <paramref name="fileName" /> with <paramref name="arguments" /> (passed verbatim, never
    ///     shell-joined), capturing stdout/stderr.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A non-zero exit code is returned in the result, not thrown. Exceeding
    ///         <paramref name="timeout" /> kills the process tree and throws
    ///         <see cref="ProcessTimeoutException" />; a missing executable surfaces as a
    ///         <see cref="System.ComponentModel.Win32Exception" />.
    ///     </para>
    ///     <para>
    ///         <paramref name="onOutputLine" />, when given, is called with each complete line of standard
    ///         output as it arrives. Standard error gets no callback, which keeps the callback from being
    ///         invoked concurrently by the two readers that drain the pipes in parallel.
    ///     </para>
    ///     <para>
    ///         It runs on the read loop that keeps the child from blocking on a full pipe, so it must be prompt
    ///         and must not throw. It may also fire after this method has returned or thrown, because the
    ///         timeout path unwinds while the readers are still live, so a caller has to be able to take a late
    ///         line harmlessly.
    ///     </para>
    /// </remarks>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string>? onOutputLine = null);
}