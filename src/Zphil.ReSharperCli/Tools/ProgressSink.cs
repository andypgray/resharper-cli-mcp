using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Zphil.ReSharperCli.Tools;

/// <summary>
///     The SDK's progress channel as the plain string sink the services take: every line handed to
///     <see cref="Send" /> becomes one <c>notifications/progress</c> frame, numbered and written in the order
///     the lines arrived. The one place in this server where a run's advance becomes an MCP notification, and
///     the reason <c>Services/</c> and <c>Execution/</c> import no MCP types.
/// </summary>
/// <remarks>
///     <para>
///         <strong>The order is the whole reason this is a class.</strong> MCP asks exactly one thing of
///         <c>progress</c> — that it increase — and a counter assigned before the send does not deliver it.
///         The SDK's own <c>IProgress&lt;ProgressNotificationValue&gt;</c> ends at <c>TokenProgress.Report</c>,
///         which starts a send and discards the task, so two perfectly ordered <c>Report</c> calls race for the
///         transport's send lock and can reach the wire transposed — measured on this server's own output
///         stream, not inferred. Here the counter is assigned <em>inside</em> a chain where each send awaits
///         the one before it, so counter order is send order is wire order. <see cref="Send" /> stays a prompt
///         <c>Action&lt;string&gt;</c> for the timer thread that calls it: it links the send onto the chain and
///         returns, and the transport write never runs on the caller's thread or under this class's lock.
///     </para>
///     <para>
///         <strong>The drain is what keeps the last frame ahead of the result.</strong>
///         <see cref="DisposeAsync" /> closes the sink and awaits whatever it has already accepted, and a tool
///         method disposes it after its return value is built and before the SDK writes the result frame. A
///         line arriving after that writes nothing rather than reporting against an answered request.
///     </para>
///     <para>
///         <see cref="For" /> answers a sink that sends nothing — rather than no sink — for a client that
///         asked for no progress. That is load-bearing: the heartbeat writing to it runs either way, and that
///         heartbeat is what keeps a file count for the timeout message even for a client that never asked to
///         watch.
///     </para>
///     <para>
///         The counter counts notifications rather than files on purpose: <c>jb</c>'s two sweeps report
///         different file totals for the same solution, so a file-derived counter would fall back to zero
///         halfway through a run. There is deliberately no <c>total</c> — see
///         <see cref="Formatting.RunProgressFormatter" />.
///     </para>
/// </remarks>
/// <param name="send">
///     Writes one notification and completes when it is on the wire. A delegate rather than the
///     <see cref="McpServer" /> itself so the ordering can be tested without one.
/// </param>
/// <param name="logger">
///     The caller's own. Required rather than optional for the reason every other logger in this codebase is:
///     a site that forgets it loses the record silently.
/// </param>
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
    ///     The sink for one tool call.
    /// </summary>
    /// <remarks>
    ///     <see langword="null" /> when there is no request context at all — a direct call rather than one the
    ///     SDK dispatched. Answering <see langword="null" /> there mirrors
    ///     <see cref="Execution.JbRunProgress.Reporting" />: a caller with nowhere to report to gets nothing
    ///     to dispose rather than a reporter that drops its lines.
    /// </remarks>
    internal static ProgressSink? For(RequestContext<CallToolRequestParams>? context, ILogger logger)
    {
        if (context is null) return null;

        // No token means the client asked for no progress. A sink that accepts and discards keeps the
        // heartbeat above it running — see the class remarks for why that is worth the discarded messages.
        // The annotation on Params says non-null and the SDK's own obsolete RequestContext constructor
        // leaves it default anyway, which is why RequestServiceProvider reads it through `?.` too. A tool
        // call may not fail over a channel its caller never asked for, so the annotation is not trusted.
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
    ///     Prompt by contract — it links the send on and returns — because it is called from a timer callback,
    ///     and disposing a timer waits for a callback in flight.
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
    ///     One link of the chain: waits for <paramref name="previous" />, takes the next counter value, and
    ///     writes.
    /// </summary>
    /// <remarks>
    ///     A send that throws still consumes its value — reusing it would repeat a number on a wire that may
    ///     already carry the frame it was spent on.
    /// </remarks>
    private async Task SendAfterAsync(Task previous, string message)
    {
        // Off the caller's thread before anything else. This is invoked under _gate, so without the yield the
        // wait for the previous send would happen there, and a slow transport would stall jb's own progress.
        // ForceYielding rather than Task.Yield() because that one captures whatever synchronization context
        // the caller happens to be on, and this runs from a timer thread that has no business dictating where
        // a transport write lands. It is the SDK's own spelling for the same reason.
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