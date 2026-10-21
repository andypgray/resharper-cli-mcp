using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Records everything the server wrote to its transport, in the order it wrote it: the one place a
///     progress contract is a fact rather than an inference about thread-pool scheduling.
/// </summary>
/// <remarks>
///     <para>
///         A client's arrival order carries no information about a server's emission order, so an ordering
///         assertion made client-side reads a promise the transport never gave. In ModelContextProtocol 2.2.0,
///         <c>McpSessionHandler.ProcessMessagesCoreAsync</c> dispatches each inbound message with a bare
///         <c>_ = ProcessMessageAsync()</c>, and that method forces a yield
///         (<c>await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding)</c>) before running
///         a handler, so every notification becomes an independent thread-pool work item.
///         <c>CallToolAsync(progress:)</c> registers an ordinary notification handler and rides that same path.
///         The scrambling happens in practice: a monotonic pin read client-side fails on ordinary full-suite runs.
///     </para>
///     <para>
///         The write side is ordered. <c>StreamServerTransport.SendMessageAsync</c> holds a single
///         <c>SemaphoreSlim</c> across the whole send (serialize, write the UTF-8 JSON, write <c>"\n"</c>,
///         flush), so frames never interleave, one line is one frame, and write order is emission order. That
///         also keeps the decode free of torn reads: a frame still being written has no newline yet, so dropping
///         the trailing partial line drops only the one that is incomplete.
///     </para>
///     <para>
///         The write side does not settle which send reaches that semaphore first. Two sends started in order
///         race for it, so ordering has to be decided before the transport, which is <c>ProgressSink</c>'s job.
///         An order read here is therefore the order the server promises, not a coincidence of scheduling.
///     </para>
/// </remarks>
internal sealed class WireLog
{
    /// <summary>What anchors <see cref="ToolResultIndex" />.</summary>
    /// <remarks>
    ///     Only a <c>CallToolResult</c> carries <c>content</c>, so anchoring here rather than on the last
    ///     response frame stops a ping or a capability response landing afterwards from moving the anchor.
    /// </remarks>
    private const string ToolResultProperty = "content";

    private readonly MemoryStream _bytes = new();
    private readonly Lock _gate = new();

    /// <summary>Gets every completed frame, in the order the server wrote it.</summary>
    public IReadOnlyList<WireFrame> Frames
    {
        get
        {
            byte[] snapshot;
            lock (_gate)
            {
                snapshot = _bytes.ToArray();
            }

            // The final element is whatever is mid-write, with no newline closing it yet, so it is not a frame.
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
    ///     Gets the <c>progress</c> value of every progress notification, in the order the server wrote them.
    /// </summary>
    /// <remarks>
    ///     A frame whose <c>progress</c> is missing or non-numeric reads as <see langword="null" /> rather than
    ///     being dropped, so a malformed beat fails a pin instead of vanishing from it.
    /// </remarks>
    public IReadOnlyList<double?> ProgressValues => Frames
        .Where(frame => frame.IsProgressNotification)
        .Select(frame => frame.ProgressValue)
        .ToList();

    /// <summary>Gets where the last progress notification sits among <see cref="Frames" />.</summary>
    /// <remarks><c>-1</c> when the server wrote none.</remarks>
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

    /// <summary>Gets where the <c>tools/call</c> result sits among <see cref="Frames" />.</summary>
    /// <remarks>
    ///     <c>-1</c> when the server has not written one. See <see cref="ToolResultProperty" /> for what
    ///     identifies it.
    /// </remarks>
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

    /// <summary>
    ///     Copies <paramref name="bytes" /> on their way past, under the lock the decode snapshots under.
    /// </summary>
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

/// <summary>
///     Represents one completed JSON-RPC frame, read off the wire. <paramref name="Method" /> is
///     <see langword="null" /> for a response, and <paramref name="IsToolResult" /> marks a <c>tools/call</c>
///     result: a <c>result</c> carrying <c>content</c>.
/// </summary>
internal sealed record WireFrame(string? Method, bool IsToolResult, double? ProgressValue)
{
    /// <summary>
    ///     Gets a value indicating whether the frame is a progress notification, by the SDK's own spelling of
    ///     the method name.
    /// </summary>
    public bool IsProgressNotification => Method == NotificationMethods.ProgressNotification;
}