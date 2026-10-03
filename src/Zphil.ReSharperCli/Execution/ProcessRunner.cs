using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Formatting;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Spawns an external process directly (no shell), captures its output, and enforces a timeout by
///     killing the whole process tree. This is the only class in the server that starts a process.
/// </summary>
/// <remarks>
///     <para>
///         It logs the mechanics of a spawn — the command line, the wall clock, the exit code, a tree killed
///         at the cap or an output drain cut at it — and does so at <c>Debug</c>, because this layer cannot
///         tell one spawn from another: a <c>jb inspectcode</c> the user is waiting on and a
///         <c>jb inspectcode --version</c> probe arrive here identically, and an <c>Information</c> line here would
///         report the probe as a run.
///     </para>
///     <para>
///         <see cref="ChildProcessLifetime" /> owns the spawn itself, so that a child cannot outlive this
///         server. That can put a platform wrapper between the caller's command and the process, which
///         splits the two names a line can use: the "Starting" line takes the <em>effective</em> command,
///         because its job is to be reproducible by hand, and every other line and message takes the name
///         the caller asked for — a Linux timeout reporting <c>'setpriv' timed out</c> would name a program
///         the caller has never heard of.
///     </para>
/// </remarks>
internal sealed class ProcessRunner(ChildProcessLifetime childLifetime, ILogger<ProcessRunner> logger) : IProcessRunner
{
    /// <summary>Cap captured stdout/stderr at 10&#160;MB each; past the cap we keep draining but stop appending.</summary>
    private const int MaxCapturedChars = 10 * 1024 * 1024;

    /// <summary>How much of a pipe is taken in one read.</summary>
    private const int ReadChunkChars = 8192;

    /// <summary>
    ///     The most of one line that is carried across chunk boundaries for a line observer. Generous against
    ///     any real line — <c>jb</c>'s longest is a file path — and the reason a stream that never emits a
    ///     newline cannot grow the carry to the size of the whole output.
    /// </summary>
    private const int MaxCarriedLineChars = 8192;

    /// <summary>
    ///     How long a killed process tree is given to be reaped before this gives up on it and unwinds. Named
    ///     rather than left inline because it is the width of a window the rest of the server can see: a run
    ///     cancelled here holds its cache-generation lease until this has elapsed, so
    ///     <c>CacheTransplanter</c> derives from it how long to wait for a donor that a caller may itself
    ///     have just cancelled.
    /// </summary>
    internal static readonly TimeSpan KilledTreeReapBudget = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string>? onOutputLine = null)
    {
        SpawnCommand command = childLifetime.Rewrite(fileName, arguments);

        ProcessStartInfo startInfo = new()
        {
            FileName = command.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (string argument in command.Arguments) startInfo.ArgumentList.Add(argument);

        using Process process = new();
        process.StartInfo = startInfo;

        // The full command line, and the only place it is ever written out: it is the difference between
        // reading "a jb run took nine minutes" and being able to reproduce that run by hand.
        logger.LogDebug("Starting {FileName} {Arguments}", command.FileName, command.Arguments);
        var elapsed = Stopwatch.StartNew();

        // A missing executable throws Win32Exception here — deliberately allowed to propagate.
        childLifetime.Start(process, command.Wrapped);

        // Close our end of stdin at once so the child sees EOF instead of inheriting — and blocking a
        // reader on — the MCP server's own JSON-RPC stdin handle.
        process.StandardInput.Close();

        // Cancelled wherever this call gives up on its readers: at the cap, which is how they hand back what they
        // read, and by the finally below on every way out, so a process the child started that still holds a pipe
        // cannot keep a reader, and all it captured, alive past the call. Deliberately left undisposed: it owns no
        // timer and no links, and a reader can still be holding its token after this method has thrown.
        CancellationTokenSource readerCut = new();

        // Drain both pipes concurrently and immediately so a chatty child never blocks on a full buffer.
        Task<string> standardOutputTask = ReadCappedAsync(process.StandardOutput, readerCut.Token, onOutputLine);
        Task<string> standardErrorTask = ReadCappedAsync(process.StandardError, readerCut.Token);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillTree(process);

                // Brief reap so the killed tree is cleaned up and the pipe readers reach EOF.
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(KilledTreeReapBudget).ConfigureAwait(false);
                }
                catch
                {
                    // The reap itself timed out or faulted; nothing more we can usefully do.
                }

                // Both endings say the tree was killed, and which of the two it was matters: a caller standing a
                // speculative pass down looks nothing like a run that ran out of budget, and only this frame can
                // still tell them apart.
                logger.LogDebug(
                    "Killed the {FileName} process tree after {ElapsedMs} ms — {Reason}",
                    fileName,
                    elapsed.ElapsedMilliseconds,
                    cancellationToken.IsCancellationRequested ? "cancelled by its caller" : $"the {DurationFormatter.Format(timeout)} cap");

                // External cancellation (the caller's token) propagates as a normal OperationCanceledException.
                if (cancellationToken.IsCancellationRequested) throw;

                throw new ProcessTimeoutException($"'{fileName}' timed out after {DurationFormatter.Format(timeout)}.");
            }

            // The process has exited and its exit code is final. Bound the pipe drain by the still-armed
            // timeout so a leaked grandchild holding a pipe open can't hang the call past `timeout`. At the cap the
            // readers are cut, and each hands back what it read before then; a caller cancelling still cancels the
            // call.
            try
            {
                await Task.WhenAll(standardOutputTask, standardErrorTask).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await readerCut.CancelAsync().ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) throw;

                logger.LogDebug(
                    "{FileName} exited, but a process it started still held its output open at the {Cap} cap; keeping what was read before it",
                    fileName,
                    DurationFormatter.Format(timeout));
            }

            string standardOutput = await standardOutputTask.ConfigureAwait(false);
            string standardError = await standardErrorTask.ConfigureAwait(false);

            logger.LogDebug(
                "{FileName} exited with code {ExitCode} after {ElapsedMs} ms",
                fileName,
                process.ExitCode,
                elapsed.ElapsedMilliseconds);

            return new ProcessResult(process.ExitCode, standardOutput, standardError);
        }
        finally
        {
            // On the kill path this runs after the reap, so the lines that arrive during it still count towards a
            // timeout's message.
            await readerCut.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Reads a redirected stream until EOF or until <paramref name="cut" /> is cancelled, keeping at most
    ///     <see cref="MaxCapturedChars" /> characters but draining the rest so the child process never blocks on
    ///     a full pipe, and returns what was kept either way.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         When <paramref name="onLine" /> is given, each complete line is handed to it as it arrives — the
    ///         same stream, observed in flight as well as captured.
    ///     </para>
    ///     <para>
    ///         Chunks fall wherever the pipe happens to break, so a line routinely straddles two of them and the
    ///         tail of a chunk has to be carried into the next. That carry is bounded by
    ///         <see cref="MaxCarriedLineChars" />: a stream with no newline in it at all would otherwise grow one
    ///         line to the size of the whole output.
    ///     </para>
    ///     <para>
    ///         A cut is the call giving up on a pipe that a process the child started still holds, at the cap or
    ///         as the call throws, and it leaves two edges. Up to one <see cref="StreamReader" /> buffer (about
    ///         4&#160;KB) can still be lost: a read that fills that buffer reads again within the same call, and the
    ///         characters it has decoded but not yet returned are dropped at the cut. And after a cut nothing
    ///         drains the pipe, so a descendant that keeps writing blocks once the pipe fills, until the read end
    ///         is finalized.
    ///     </para>
    /// </remarks>
    private async Task<string> ReadCappedAsync(StreamReader reader, CancellationToken cut, Action<string>? onLine = null)
    {
        StringBuilder builder = new();
        var buffer = new char[ReadChunkChars];

        // Only allocated when someone is watching, so the ordinary capture-only read is exactly what it was.
        StringBuilder? carry = onLine is null ? null : new StringBuilder();

        try
        {
            int read;

            // The token normally cancels the blocked pipe read itself: Windows aborts it with CancelSynchronousIo,
            // and on Unix the read is a socket receive that takes the token. WaitAsync is the backstop, so the
            // loop still leaves at the cut if that cancellation lands late.
            while ((read = await reader.ReadAsync(buffer.AsMemory(), cut).AsTask().WaitAsync(cut).ConfigureAwait(false)) > 0)
            {
                int remaining = MaxCapturedChars - builder.Length;
                if (remaining > 0) builder.Append(buffer, 0, Math.Min(read, remaining));

                if (carry is not null) EmitLines(buffer.AsSpan(0, read), carry, onLine!);
            }
        }
        catch (IOException)
        {
            // The pipe was torn down (e.g. the process was killed on timeout); return what we captured.
        }
        catch (OperationCanceledException) when (cut.IsCancellationRequested)
        {
            // The call gave up on the pipe; return what we captured.
        }

        // A last line with no newline after it is still a line: the shape a killed process tends to leave, and
        // at a cut possibly only the part of one that arrived before it.
        if (carry is { Length: > 0 }) EmitLine(carry, onLine!);

        return builder.ToString();
    }

    /// <summary>
    ///     Split <paramref name="chunk" /> on newlines, emitting each complete line and leaving the remainder
    ///     in <paramref name="carry" /> for the next chunk.
    /// </summary>
    private void EmitLines(ReadOnlySpan<char> chunk, StringBuilder carry, Action<string> onLine)
    {
        while (true)
        {
            int newline = chunk.IndexOf('\n');
            if (newline < 0) break;

            Append(carry, chunk[..newline]);
            EmitLine(carry, onLine);
            chunk = chunk[(newline + 1)..];
        }

        Append(carry, chunk);
    }

    /// <summary>
    ///     Hand what has been carried so far to <paramref name="onLine" /> as one line, and reset the carry.
    ///     <c>jb</c> writes CRLF, so the trailing carriage return is dropped here rather than left for every
    ///     consumer to trim — off the carry before materializing, since trimming the string instead would
    ///     recopy every line on a stream where every line ends in one.
    /// </summary>
    private void EmitLine(StringBuilder carry, Action<string> onLine)
    {
        if (carry.Length > 0 && carry[^1] == '\r') carry.Length--;

        var line = carry.ToString();
        carry.Clear();

        try
        {
            onLine(line);
        }
        catch (Exception exception)
        {
            // This runs on the loop that keeps the child from blocking on a full pipe, so a throwing observer
            // must not be able to stop the drain. Debug because the caller in this server — JbRunProgress —
            // is documented never to throw, which makes anything here a defect rather than an expected state.
            logger.LogDebug(exception, "An output-line observer threw while draining a child process; the drain continues");
        }
    }

    /// <summary>
    ///     Add <paramref name="text" /> to the carried line, stopping at <see cref="MaxCarriedLineChars" />.
    ///     Past the bound the rest of that line is dropped and the next newline resynchronises, so a stream
    ///     with no line breaks costs a bounded buffer rather than an unbounded one.
    /// </summary>
    private static void Append(StringBuilder carry, ReadOnlySpan<char> text)
    {
        int room = MaxCarriedLineChars - carry.Length;
        if (room <= 0) return;

        carry.Append(text.Length <= room ? text : text[..room]);
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (InvalidOperationException)
        {
            // The process already exited between the timeout firing and this kill — nothing to do.
        }
    }
}