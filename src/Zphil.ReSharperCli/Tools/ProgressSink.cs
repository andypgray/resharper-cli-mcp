using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Zphil.ReSharperCli.Tools;

/// <summary>
///     Adapts the SDK's progress channel to the plain string sink the services take: every line handed to
///     <see cref="Send" /> becomes one <c>notifications/progress</c> frame, numbered and written in the order
///     the lines arrived.
/// </summary>
/// <remarks>
///     <para>
///         This is the one place a run's advance becomes an MCP notification, which keeps <c>Services/</c> and
///         <c>Execution/</c> free of MCP types.
///     </para>
///     <para>
///         MCP requires <c>progress</c> to increase, and a counter assigned before the send does not deliver
///         that. The SDK's own <c>IProgress&lt;ProgressNotificationValue&gt;</c> ends at
///         <c>TokenProgress.Report</c>, which starts a send and discards the task, so two ordered <c>Report</c>
///         calls race for the transport's send lock and can reach the wire transposed. Here the counter is
///         assigned inside a chain where each send awaits the one before it, so counter order is send order is
///         wire order.
///     </para>
///     <para>
///         The counter counts notifications rather than files, because <c>jb</c>'s sweeps report different
///         file totals for the same solution, so a file-derived counter would fall back to zero halfway through
///         a run. No <c>total</c> is sent either: <c>jb</c> announces no file count up front, and a client draws
///         <c>total</c> as a filling bar, so the one denominator available, elapsed time against the run cap,
///         would read as work done while meaning budget consumed.
///     </para>
///     <para>
///         <paramref name="send" /> must complete only once its frame is on the wire, since the next link waits
///         on it. It is a delegate rather than the <see cref="McpServer" /> so the ordering can be tested
///         without one.
///     </para>
/// </remarks>
internal sealed class ProgressSink(Func<int, string, Task> send, ILogger logger) : IAsyncDisposable
{
    private readonly Lock _gate = new();

    private bool _closed;

    /// <summary>
    ///     How many notifications this call has sent. Read and written only from inside the chain, where one
    ///     link runs at a time and each awaits the last, so it needs no interlocked access of its own.
    /// </summary>
    private int _sent;

    /// <summary>
    ///     The last send linked onto the chain — what the next one waits for, and what
    ///     <see cref="DisposeAsync" /> drains. Never faults: every link catches its own send.
    /// </summary>
    private Task _tail = Task.CompletedTask;

    /// <summary>
    ///     Closes the sink and waits out everything it has already accepted, so no frame can be written after
    ///     the result of the call this sink belongs to.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task tail;
        lock (_gate)
        {
            // Both under one lock, which is what makes the drain complete: a line that got in before the
            // close is in the chain this captures, and one that arrives after writes nothing at all.
            _closed = true;
            tail = _tail;
        }

        await tail.ConfigureAwait(false);
    }

    /// <summary>
    ///     Returns the sink for one tool call.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see langword="null" /> when there is no request context at all, as in a direct call rather than
    ///         one the SDK dispatched. That mirrors <see cref="Execution.JbRunProgress.Reporting" />: a caller
    ///         with nowhere to report to gets nothing to dispose rather than a reporter that drops its lines.
    ///     </para>
    ///     <para>
    ///         A client that asked for no progress gets a sink that sends nothing rather than no sink, because
    ///         the heartbeat writing to it runs either way, and that heartbeat is what keeps a file count for the
    ///         timeout message.
    ///     </para>
    /// </remarks>
    internal static ProgressSink? For(RequestContext<CallToolRequestParams>? context, ILogger logger)
    {
        if (context is null) return null;

        // No token means the client asked for no progress. The annotation on Params says non-null, but the
        // SDK's own obsolete RequestContext constructor leaves it default, which is why RequestServiceProvider
        // reads it through `?.` too; a tool call may not fail over a channel its caller never asked for, so the
        // annotation is not trusted.
        // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
        if (context.Params?.ProgressToken is not { } token)
            return new ProgressSink(static (_, _) => Task.CompletedTask, logger);

        McpServer server = context.Server;

        return new ProgressSink(
            (value, message) => server.NotifyProgressAsync(
                token,
                new ProgressNotificationValue { Progress = value, Message = message }),
            logger);
    }

    /// <summary>
    ///     Queues <paramref name="message" /> as the next notification.
    /// </summary>
    /// <remarks>
    ///     Prompt by contract, linking the send on and returning, because it is called from a timer callback and
    ///     disposing a timer waits for a callback in flight. The transport write never runs on the caller's
    ///     thread or under this class's lock.
    /// </remarks>
    internal void Send(string message)
    {
        lock (_gate)
        {
            if (_closed) return;

            _tail = SendAfterAsync(_tail, message);
        }
    }

    /// <summary>
    ///     Runs one link of the chain: waits for <paramref name="previous" />, takes the next counter value, and
    ///     writes.
    /// </summary>
    /// <remarks>
    ///     A send that throws still consumes its value — reusing it would repeat a number on a wire that may
    ///     already carry the frame it was spent on.
    /// </remarks>
    private async Task SendAfterAsync(Task previous, string message)
    {
        // Off the caller's thread before anything else. This is invoked under _gate, so without the yield a
        // send whose predecessor had finished would start there, on the caller's thread, and a slow transport
        // write would stall the heartbeat that called it. ForceYielding rather than Task.Yield(), because
        // Task.Yield captures whatever synchronization context the caller is on, and a timer thread has no
        // business dictating where a transport write lands. It is the SDK's own spelling for the same reason.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        await previous.ConfigureAwait(false);

        int value = ++_sent;

        try
        {
            await send(value, message).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Debug, and swallowed: progress is an optimisation over silence, and an optimisation may not
            // fail the call it is reporting on. A client that went away mid-run reaches here every beat.
            logger.LogDebug(exception, "Could not send progress notification {Progress}", value);
        }
    }
}