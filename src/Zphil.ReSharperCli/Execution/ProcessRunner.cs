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
///         at the cap — and does so at <c>Debug</c>, because this layer cannot tell one spawn from another. A
///         <c>jb inspectcode</c> the user is waiting on and the <c>jb inspectcode --version</c> probe
///         <c>JbLocator</c> makes arrive here identically, and an <c>Information</c> line here would report
///         the probe as a run. The single <c>Information</c> line per <c>jb</c> run belongs one level up, in
///         <see cref="Services.JbRunner" />, which knows which is which and knows what the cache looked like
///         going in.
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

        // Drain both pipes concurrently and immediately so a chatty child never blocks on a full buffer.
        Task<string> standardOutputTask = ReadCappedAsync(process.StandardOutput, onOutputLine);
        Task<string> standardErrorTask = ReadCappedAsync(process.StandardError);

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
        // timeout so a leaked grandchild holding a pipe open can't hang the call past `timeout`.
        string standardOutput = await DrainWithinBudgetAsync(standardOutputTask, timeoutCts.Token, cancellationToken).ConfigureAwait(false);
        string standardError = await DrainWithinBudgetAsync(standardErrorTask, timeoutCts.Token, cancellationToken).ConfigureAwait(false);

        logger.LogDebug(
            "{FileName} exited with code {ExitCode} after {ElapsedMs} ms",
            fileName,
            process.ExitCode,
            elapsed.ElapsedMilliseconds);

        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    /// <summary>
    ///     Await a pipe reader within the remaining timeout budget. The process has already exited; if a
    ///     leaked grandchild is still holding the write end open the reader never reaches EOF, so cap the
    ///     wait on <paramref name="timeoutToken" /> and fall back to empty — stdout/stderr are advisory
    ///     (inspect results come from the SARIF file) and the real exit code is already in hand. External
    ///     cancellation is re-thrown so the caller's token still cancels the call.
    /// </summary>
    private static async Task<string> DrainWithinBudgetAsync(
        Task<string> readerTask,
        CancellationToken timeoutToken,
        CancellationToken cancellationToken)
    {
        try
        {
            return await readerTask.WaitAsync(timeoutToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;

            return string.Empty;
        }
    }

    /// <summary>
    ///     Read a redirected stream to EOF, keeping at most <see cref="MaxCapturedChars" /> characters but
    ///     always draining the rest so the child process never blocks on a full pipe. When
    ///     <paramref name="onLine" /> is given, each complete line is handed to it as it arrives — the same
    ///     stream, observed in flight as well as captured.
    /// </summary>
    /// <remarks>
    ///     Chunks fall wherever the pipe happens to break, so a line routinely straddles two of them and the
    ///     tail of a chunk has to be carried into the next. That carry is bounded by
    ///     <see cref="MaxCarriedLineChars" />: a stream with no newline in it at all would otherwise grow one
    ///     line to the size of the whole output.
    /// </remarks>
    private async Task<string> ReadCappedAsync(StreamReader reader, Action<string>? onLine = null)
    {
        StringBuilder builder = new();
        var buffer = new char[ReadChunkChars];

        // Only allocated when someone is watching, so the ordinary capture-only read is exactly what it was.
        StringBuilder? carry = onLine is null ? null : new StringBuilder();

        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
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

        // A last line with no newline after it — the shape a killed process tends to leave — is still a line.
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