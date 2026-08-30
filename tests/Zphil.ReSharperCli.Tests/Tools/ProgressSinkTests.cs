using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Tests.TestSupport;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Tests.Tools;

/// <summary>
///     The ordering the wire pin can only observe: that the number on a notification is the number the one
///     before it was followed by, however many threads are handing lines in and however unevenly the sends
///     complete.
/// </summary>
/// <remarks>
///     This is the layer the transposition lived in. A counter taken before the send lets two callers swap
///     between taking a value and reaching the transport, and no client-side or wire-side assertion can tell
///     that apart from a slow send — which is why the fix is pinned here, on the values the sink itself
///     produced, rather than only end to end in <c>ProgressNotificationTests</c>.
/// </remarks>
public sealed class ProgressSinkTests
{
    /// <summary>Long enough that only a genuine hang reaches it.</summary>
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Send_ManyThreadsAtOnce_NumbersEveryNotificationInTheOrderItIsWritten()
    {
        // Arrange — the difficulty is that every link starts with a forced yield, so a send is always still
        // outstanding when the next line arrives: a counter taken before the send is one that can be overtaken.
        const int lines = 200;
        Sends sends = new();

        // Act — every thread the pool will give us, all pushing at once.
        await using (ProgressSink sink = new(sends.SendAsync, NullLogger.Instance))
        {
            await Parallel.ForAsync(0, lines, Ct, async (index, _) =>
            {
                await Task.Yield();
                sink.Send($"line {index}");
            });
        }

        // Assert — contiguous from one and in that order, which is the one thing MCP asks of `progress`. The
        // values are read in the order the sends started, so this fails if a later number reached one first.
        IReadOnlyList<int> written = sends.Items.Select(sent => sent.Value).ToList();
        written.Count.ShouldBe(lines);
        written.ShouldBe(Enumerable.Range(1, lines));
    }

    [Fact]
    public async Task Send_OneSlowSend_DoesNotLetTheNextOneOvertakeIt()
    {
        // Arrange — the transposition in miniature: the first send is held open while a second is queued, so
        // a sink that numbered outside the send would hand out 2 and let it reach the wire first.
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Sends sends = new() { Hold = release.Task };

        await using ProgressSink sink = new(sends.SendAsync, NullLogger.Instance);

        // Act
        sink.Send("first");
        await sends.WaitForAsync(1, Ct);
        sink.Send("second");

        // Assert — the second send has not started, let alone taken a number, while the first is in flight.
        await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
        sends.Count.ShouldBe(1);

        release.SetResult();
        await sends.WaitForAsync(2, Ct);
        sends.Items.Select(sent => (sent.Value, sent.Message))
            .ShouldBe([(1, "first"), (2, "second")]);
    }

    [Fact]
    public async Task DisposeAsync_ASendStillInFlight_DrainsItBeforeReturning()
    {
        // Arrange — this is what keeps the last notification ahead of the result frame: the tool method
        // disposes the sink after its answer is built and before the SDK writes it.
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Sends sends = new() { Hold = release.Task };
        ProgressSink sink = new(sends.SendAsync, NullLogger.Instance);

        sink.Send("queued");
        await sends.WaitForAsync(1, Ct);

        // Act — disposal cannot complete while the send is held.
        ValueTask disposal = sink.DisposeAsync();
        disposal.IsCompleted.ShouldBeFalse();

        release.SetResult();
        await disposal;

        // Assert
        sends.Items.ShouldHaveSingleItem().Message.ShouldBe("queued");
    }

    [Fact]
    public async Task Send_AfterDisposal_WritesNothing()
    {
        // Arrange — the run's heartbeat is stopped before this point, but a line arriving late has to be
        // harmless rather than merely unlikely: written now it would land after the result.
        Sends sends = new();
        ProgressSink sink = new(sends.SendAsync, NullLogger.Instance);
        await sink.DisposeAsync();

        // Act
        sink.Send("too late");

        // Assert — and nothing turns up afterwards either, which a queued send would.
        await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
        sends.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Send_TheSendThrows_KeepsGoingAndDoesNotReuseTheNumber()
    {
        // Arrange — a client that went away mid-run faults every send from then on. A value spent on a frame
        // that may already be half-written cannot be handed out again.
        Sends sends = new() { ThrowOn = 2 };

        // Act
        await using (ProgressSink sink = new(sends.SendAsync, NullLogger.Instance))
        {
            sink.Send("first");
            sink.Send("second");
            sink.Send("third");
        }

        // Assert — three attempts, three distinct numbers, and the failure neither stopped the chain nor
        // renumbered what followed it.
        sends.Items.Select(sent => sent.Value).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void For_NoRequestContextAtAll_IsNull()
    {
        // A direct call rather than one the SDK dispatched — the tool methods' own tests. Answering null
        // leaves a call site with one nullable sink to await-using rather than a branch around the feature.
        ProgressSink.For(null, NullLogger.Instance).ShouldBeNull();
    }

    [Fact]
    public async Task For_AClientThatSentNoProgressToken_IsASinkThatAcceptsAndDiscards()
    {
        // Arrange — load-bearing, and the behaviour NullProgress.Instance had when the SDK chose between the
        // two: the heartbeat above this still has to run, because it is what leaves JbRunProgress with a file
        // count for the timeout message even for a client that never asked to watch.
        RequestContext<CallToolRequestParams> context =
            Dispatched(new CallToolRequestParams { Name = "resharper_inspect" });

        // Act
        ProgressSink? sink = ProgressSink.For(context, NullLogger.Instance);

        // Assert — a sink, not a null one, and one that swallows rather than throwing on a missing token.
        sink.ShouldNotBeNull();
        sink.Send("nobody is listening");
        await sink.DisposeAsync();
    }

    [Fact]
    public async Task For_ARequestCarryingNoParamsAtAll_IsStillASinkRatherThanAThrow()
    {
        // Arrange — a context with no params is constructible, and the SDK guards its own read of them the
        // same way. Throwing here would fail a tool call over a channel the caller never asked for.
        RequestContext<CallToolRequestParams> context = Dispatched(null!);

        // Act
        ProgressSink? sink = ProgressSink.For(context, NullLogger.Instance);

        // Assert
        sink.ShouldNotBeNull();
        sink.Send("nobody is listening");
        await sink.DisposeAsync();
    }

    /// <summary>A request context of the shape the SDK binds into a tool method, carrying <paramref name="parameters" />.</summary>
    private static RequestContext<CallToolRequestParams> Dispatched(CallToolRequestParams parameters)
    {
        return new RequestContext<CallToolRequestParams>(
            Substitute.For<McpServer>(), new JsonRpcRequest { Method = "tools/call" }, parameters);
    }

    /// <summary>What the sink asked for, in the order the sends completed.</summary>
    /// <param name="Value">The <c>progress</c> counter the sink assigned.</param>
    /// <param name="Message">The line it was assigned to.</param>
    private sealed record Sent(int Value, string Message);

    /// <summary>
    ///     A send that records what it was given and can be made slow or failing. Recording happens on entry
    ///     rather than on completion so a held send is observable while it is still in flight — which is also
    ///     what makes <see cref="RecordingSink{T}.Items" /> the order the sends were started in.
    /// </summary>
    private sealed class Sends() : RecordingSink<Sent>(Generous)
    {
        /// <summary>When set, every send waits for this before completing.</summary>
        public Task? Hold { get; init; }

        /// <summary>The counter value whose send throws, standing in for a client that went away.</summary>
        public int ThrowOn { get; init; }

        public async Task SendAsync(int value, string message)
        {
            Record(new Sent(value, message));

            if (Hold is { } hold) await hold;

            if (value == ThrowOn) throw new InvalidOperationException("The client went away mid-run.");
        }
    }
}