using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Spawns an external process directly (no shell), captures its output, and enforces a timeout by
///     killing the whole process tree. This is the only class in the server that starts a process.
/// </summary>
/// <remarks>
///     It logs the mechanics of a spawn — the command line, the wall clock, the exit code, a tree killed at
///     the cap — and does so at <c>Debug</c>, because this layer cannot tell one spawn from another. A
///     <c>jb inspectcode</c> the user is waiting on and the <c>jb inspectcode --version</c> probe
///     <c>JbLocator</c> makes arrive here identically, and an <c>Information</c> line here would report the
///     probe as a run. The single <c>Information</c> line per <c>jb</c> run belongs one level up, in
///     <see cref="Services.JbRunner" />, which knows which is which and knows what the cache looked like
///     going in.
/// </remarks>
internal sealed class ProcessRunner(ILogger<ProcessRunner> logger) : IProcessRunner
{
    /// <summary>Cap captured stdout/stderr at 10&#160;MB each; past the cap we keep draining but stop appending.</summary>
    private const int MaxCapturedChars = 10 * 1024 * 1024;

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
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        using Process process = new();
        process.StartInfo = startInfo;

        // The full command line, and the only place it is ever written out: it is the difference between
        // reading "a jb run took nine minutes" and being able to reproduce that run by hand.
        logger.LogDebug("Starting {FileName} {Arguments}", fileName, arguments);
        var elapsed = Stopwatch.StartNew();

        // A missing executable throws Win32Exception here — deliberately allowed to propagate.
        process.Start();

        // Close our end of stdin at once so the child sees EOF instead of inheriting — and blocking a
        // reader on — the MCP server's own JSON-RPC stdin handle.
        process.StandardInput.Close();

        // Drain both pipes concurrently and immediately so a chatty child never blocks on a full buffer.
        Task<string> standardOutputTask = ReadCappedAsync(process.StandardOutput);
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
                cancellationToken.IsCancellationRequested ? "cancelled by its caller" : $"the {FormatDuration(timeout)} cap");

            // External cancellation (the caller's token) propagates as a normal OperationCanceledException.
            if (cancellationToken.IsCancellationRequested) throw;

            throw new ProcessTimeoutException($"'{fileName}' timed out after {FormatDuration(timeout)}.");
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
    ///     Human-readable, correctly-pluralized run duration: "30 seconds", "5 minutes", "1 minute
    ///     30 seconds". The leftover seconds are spelled out rather than rounded into the minute count
    ///     because the run cap is configured <em>in</em> seconds — a cap someone set to 90 must not report
    ///     itself as two minutes, or the message contradicts the value they chose.
    /// </summary>
    internal static string FormatDuration(TimeSpan duration)
    {
        int totalSeconds = Math.Max(1, (int)Math.Round(duration.TotalSeconds, MidpointRounding.AwayFromZero));
        if (totalSeconds < 60) return Pluralize(totalSeconds, "second");

        int minutes = totalSeconds / 60;
        int seconds = totalSeconds % 60;

        return seconds == 0
            ? Pluralize(minutes, "minute")
            : $"{Pluralize(minutes, "minute")} {Pluralize(seconds, "second")}";
    }

    private static string Pluralize(int count, string unit)
    {
        return count == 1 ? $"1 {unit}" : $"{count} {unit}s";
    }

    /// <summary>
    ///     Read a redirected stream to EOF, keeping at most <see cref="MaxCapturedChars" /> characters but
    ///     always draining the rest so the child process never blocks on a full pipe.
    /// </summary>
    private static async Task<string> ReadCappedAsync(StreamReader reader)
    {
        StringBuilder builder = new();
        var buffer = new char[8192];

        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                int remaining = MaxCapturedChars - builder.Length;
                if (remaining > 0) builder.Append(buffer, 0, Math.Min(read, remaining));
            }
        }
        catch (IOException)
        {
            // The pipe was torn down (e.g. the process was killed on timeout); return what we captured.
        }

        return builder.ToString();
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