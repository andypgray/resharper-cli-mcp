using System.Text;
using Shouldly;
using Xunit;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     The negative control for <see cref="WireLog" />: proof that it reports the violation it exists to
///     report, which no green run against a correct server can give. Synthetic frames, no transport and no
///     timing, so this is the deterministic half of the contract <c>ProgressNotificationTests</c> asserts.
/// </summary>
public sealed class WireLogTests
{
    private const string ToolResultFrame =
        """{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"No issues found."}],"isError":false}}""";

    /// <summary>A response with no <c>content</c> — the anchor must not settle on one of these.</summary>
    private const string InitializeResultFrame =
        """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-06-18","capabilities":{},"serverInfo":{"name":"resharper-cli-mcp"}}}""";

    [Fact]
    public void Frames_BeatsWrittenBeforeTheResult_PutsEveryOneOfThemBeforeIt()
    {
        // Arrange & Act — the shape a correct run leaves on the wire.
        WireLog log = new();
        Write(log, ProgressFrame(1));
        Write(log, ProgressFrame(2));
        Write(log, ToolResultFrame);

        // Assert
        double?[] counted = [1, 2];
        log.ProgressValues.ShouldBe(counted);
        log.LastProgressIndex.ShouldBeLessThan(log.ToolResultIndex);
    }

    [Fact]
    public void Frames_ABeatWrittenAfterTheResult_PutsItAfter()
    {
        // Arrange & Act — the bug the wire exists to catch: a beat against a request already answered. The
        // client cannot see this one at all, because its handler registration is gone by the time it lands.
        WireLog log = new();
        Write(log, ProgressFrame(1));
        Write(log, ToolResultFrame);
        Write(log, ProgressFrame(2));

        // Assert
        log.LastProgressIndex.ShouldBeGreaterThan(log.ToolResultIndex);
    }

    [Fact]
    public void Frames_AFrameWithNoNewlineYet_IsNotReadUntilItArrives()
    {
        // Arrange — a send caught between its JSON and its terminator, which is the only torn read a reader of
        // this stream can take. Dropping the trailing partial line is what makes it harmless.
        WireLog log = new();
        Write(log, ProgressFrame(1));
        log.Append(Encoding.UTF8.GetBytes(ToolResultFrame));

        // Assert — the half-written frame is not a frame.
        log.Frames.Count.ShouldBe(1);
        log.ToolResultIndex.ShouldBe(-1);

        // Act — the terminator lands, completing it.
        log.Append("\n"u8);

        // Assert
        log.Frames.Count.ShouldBe(2);
        log.ToolResultIndex.ShouldBe(1);
    }

    [Fact]
    public void ToolResultIndex_AResponseCarryingNoContent_IsNotMistakenForTheResult()
    {
        // Arrange & Act — the handshake response precedes everything, and only a CallToolResult carries
        // `content`, so anchoring there rather than on "a response frame" keeps the anchor on the tool call.
        WireLog log = new();
        Write(log, InitializeResultFrame);
        Write(log, ProgressFrame(1));
        Write(log, ToolResultFrame);

        // Assert
        log.ToolResultIndex.ShouldBe(2);
        log.LastProgressIndex.ShouldBeLessThan(log.ToolResultIndex);
    }

    [Fact]
    public void Frames_AnEmptyLog_ReadsAsNoFramesAtAll()
    {
        // Arrange & Act
        WireLog log = new();

        // Assert — and neither index invents one.
        log.Frames.ShouldBeEmpty();
        log.LastProgressIndex.ShouldBe(-1);
        log.ToolResultIndex.ShouldBe(-1);
    }

    /// <summary>The server's own two writes per frame: the UTF-8 JSON, then the terminator.</summary>
    private static void Write(WireLog log, string frame)
    {
        log.Append(Encoding.UTF8.GetBytes(frame));
        log.Append("\n"u8);
    }

    /// <summary>Three <c>$</c>, because the frame's own trailing <c>}}</c> is content rather than an interpolation.</summary>
    private static string ProgressFrame(int counter)
    {
        return
            $$$"""{"jsonrpc":"2.0","method":"notifications/progress","params":{"progressToken":1,"progress":{{{counter}}},"message":"beat {{{counter}}}"}}""";
    }
}