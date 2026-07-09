using System.Diagnostics;
using System.Text;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Spawns an external process directly (no shell), captures its output, and enforces a timeout by
///     killing the whole process tree. This is the only class in the server that starts a process.
/// </summary>
internal sealed class ProcessRunner : IProcessRunner
{
    /// <summary>Cap captured stdout/stderr at 10&#160;MB each; past the cap we keep draining but stop appending.</summary>
    private const int MaxCapturedChars = 10 * 1024 * 1024;

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

        // A missing executable throws Win32Exception here — deliberately allowed to propagate.
        process.Start();

        // Close our end of stdin at once so the child sees EOF instead of inheriting — and blocking a
        // reader on — the MCP server's own JSON-RPC stdin handle.
        process.StandardInput.Close();

        // Drain both pipes concurrently and immediately so a chatty child never blocks on a full buffer.
        var standardOutputTask = ReadCappedAsync(process.StandardOutput);
        var standardErrorTask = ReadCappedAsync(process.StandardError);

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
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch
            {
                // The reap itself timed out or faulted; nothing more we can usefully do.
            }

            // External cancellation (the caller's token) propagates as a normal OperationCanceledException.
            if (cancellationToken.IsCancellationRequested) throw;

            throw new UserErrorException($"'{fileName}' timed out after {FormatDuration(timeout)}.");
        }

        // The process has exited and its exit code is final. Bound the pipe drain by the still-armed
        // timeout so a leaked grandchild holding a pipe open can't hang the call past `timeout`.
        string standardOutput = await DrainWithinBudgetAsync(standardOutputTask, timeoutCts.Token, cancellationToken).ConfigureAwait(false);
        string standardError = await DrainWithinBudgetAsync(standardErrorTask, timeoutCts.Token, cancellationToken).ConfigureAwait(false);

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
    ///     Human-readable, correctly-pluralized run duration: sub-minute values render in whole seconds
    ///     ("1 second", "30 seconds"); a minute or longer renders in whole minutes rounded away from zero
    ///     ("1 minute", "5 minutes").
    /// </summary>
    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 60)
        {
            int seconds = Math.Max(1, (int)Math.Round(duration.TotalSeconds, MidpointRounding.AwayFromZero));
            return seconds == 1 ? "1 second" : $"{seconds} seconds";
        }

        int minutes = Math.Max(1, (int)Math.Round(duration.TotalMinutes, MidpointRounding.AwayFromZero));
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
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