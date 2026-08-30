using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Everything the server wrote to its transport, in the order it wrote it — the one place a progress
///     contract is a fact rather than an inference about thread-pool scheduling.
/// </summary>
/// <remarks>
///     <para>
///         A client's arrival order carries no information about a server's emission order, so an ordering
///         assertion made client-side reads a promise the transport never gave. In ModelContextProtocol 2.2.0,
///         <c>McpSessionHandler.ProcessMessagesCoreAsync</c> dispatches each inbound message with a bare
///         <c>_ = ProcessMessageAsync()</c>, and that method forces a yield —
///         <c>await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding)</c> — before running
///         a handler, so every notification becomes an independent thread-pool work item.
///         <c>CallToolAsync(progress:)</c> registers an ordinary notification handler and rides that same path.
///         Measured on this machine (2026-08-25, both test platforms): the client-side monotonic pin this class
///         replaced failed 2 full-suite runs in 3, seeing <c>[2,4,3,1,5,6]</c> against an expected <c>[1..6]</c>.
///     </para>
///     <para>
///         The write side concedes nothing. <c>StreamServerTransport.SendMessageAsync</c> holds a single
///         <c>SemaphoreSlim</c> across the whole send — serialize, write the UTF-8 JSON, write <c>"\n"</c>,
///         flush — so frames never interleave, one line is one frame, and write order is emission order. That
///         also makes the decode torn-read-free for nothing: a frame still being written has no newline yet, so
///         dropping the trailing partial line drops exactly the one that is incomplete.
///     </para>
///     <para>
///         What the write side does <em>not</em> settle is which send reaches that semaphore first, and for
///         progress that is the whole contract. Two sends started in order race for it, so ordering has to be
///         decided before the transport — which is what <c>ProgressSink</c> does by awaiting each send before
///         numbering and starting the next. Reading order here is therefore reading a promise the server makes,
///         not a coincidence of scheduling: this class is what makes the promise checkable, and the sink is
///         what makes it true.
///     </para>
/// </remarks>
internal sealed class WireLog
{
    /// <summary>
    ///     What anchors <see cref="ToolResultIndex" />. Only a <c>CallToolResult</c> carries <c>content</c>, so
    ///     anchoring here rather than on the last response frame stops a ping or a capability response landing
    ///     afterwards from moving the anchor.
    /// </summary>
    private const string ToolResultProperty = "content";

    private readonly MemoryStream _bytes = new();
    private readonly Lock _gate = new();

    /// <summary>Every completed frame, in the order the server wrote it.</summary>
    public IReadOnlyList<WireFrame> Frames
    {
        get
        {
            byte[] snapshot;
            lock (_gate)
            {
                snapshot = _bytes.ToArray();
            }

            // The final element is whatever is mid-write — no newline has closed it — so it is not yet a frame.
            string[] lines = Encoding.UTF8.GetString(snapshot).Split('\n');

            return lines
                .Take(lines.Length - 1)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Select(Parse)
                .ToList();
        }
    }

    /// <summary>
    ///     The <c>progress</c> value of every progress notification, in the order the server wrote them. A frame
    ///     whose <c>progress</c> is missing or non-numeric reads as <see langword="null" /> rather than being
    ///     dropped, so a malformed beat fails a pin instead of vanishing from it.
    /// </summary>
    public IReadOnlyList<double?> ProgressValues => Frames
        .Where(frame => frame.IsProgressNotification)
        .Select(frame => frame.ProgressValue)
        .ToList();

    /// <summary>
    ///     Where the last progress notification sits among <see cref="Frames" />, or <c>-1</c> when the server
    ///     wrote none.
    /// </summary>
    public int LastProgressIndex
    {
        get
        {
            IReadOnlyList<WireFrame> frames = Frames;

            return Enumerable
                .Range(0, frames.Count)
                .LastOrDefault(index => frames[index].IsProgressNotification, -1);
        }
    }

    /// <summary>
    ///     Where the <c>tools/call</c> result sits among <see cref="Frames" />, or <c>-1</c> when the server has
    ///     not written one. See <see cref="ToolResultProperty" /> for what identifies it.
    /// </summary>
    public int ToolResultIndex
    {
        get
        {
            IReadOnlyList<WireFrame> frames = Frames;

            return Enumerable
                .Range(0, frames.Count)
                .FirstOrDefault(index => frames[index].IsToolResult, -1);
        }
    }

    /// <summary>Copy <paramref name="bytes" /> on their way past, under the lock the decode snapshots under.</summary>
    public void Append(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            _bytes.Write(bytes);
        }
    }

    private static WireFrame Parse(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;

        return new WireFrame(ReadMethod(root), ReadIsToolResult(root), ReadProgress(root));
    }

    private static string? ReadMethod(JsonElement root)
    {
        if (!root.TryGetProperty("method", out JsonElement method)) return null;
        if (method.ValueKind != JsonValueKind.String) return null;

        return method.GetString();
    }

    private static bool ReadIsToolResult(JsonElement root)
    {
        if (!root.TryGetProperty("result", out JsonElement result)) return false;
        if (result.ValueKind != JsonValueKind.Object) return false;

        return result.TryGetProperty(ToolResultProperty, out _);
    }

    private static double? ReadProgress(JsonElement root)
    {
        if (!root.TryGetProperty("params", out JsonElement parameters)) return null;
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty("progress", out JsonElement progress)) return null;
        if (progress.ValueKind != JsonValueKind.Number) return null;

        return progress.GetDouble();
    }
}

/// <summary>One completed JSON-RPC frame, read off the wire.</summary>
/// <param name="Method">The frame's <c>method</c>, or <see langword="null" /> when it is a response.</param>
/// <param name="IsToolResult">Whether the frame is a <c>tools/call</c> result — a <c>result</c> carrying <c>content</c>.</param>
/// <param name="ProgressValue">The frame's <c>params.progress</c>, or <see langword="null" /> when it has none.</param>
internal sealed record WireFrame(string? Method, bool IsToolResult, double? ProgressValue)
{
    /// <summary>Whether the frame is a progress notification, by the SDK's own spelling of the method name.</summary>
    public bool IsProgressNotification => Method == NotificationMethods.ProgressNotification;
}