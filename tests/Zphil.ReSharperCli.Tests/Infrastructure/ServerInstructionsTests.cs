using System.Text;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Infrastructure;
using Zphil.ReSharperCli.Resources;

namespace Zphil.ReSharperCli.Tests.Infrastructure;

/// <summary>
///     Guards the embedded <c>server-instructions.md</c> resource. A rename of the file or its manifest
///     resource id would otherwise surface only as a runtime failure when a client connects; these
///     load-time assertions turn it into a test failure instead.
/// </summary>
public sealed class ServerInstructionsTests
{
    [Fact]
    public void Text_LoadsAndIsNonTrivial()
    {
        // Assert
        ServerInstructions.Text.Length.ShouldBeGreaterThan(100);
    }

    [Fact]
    public void Text_ContainsUnofficialDisclaimer()
    {
        // Assert
        ServerInstructions.Text.ShouldContain("unofficial");
        ServerInstructions.Text.ShouldContain("not affiliated with or endorsed by JetBrains");
    }

    [Fact]
    public void Text_StaysUnderClaudeCodeTruncationCap()
    {
        // Claude Code silently truncates server instructions past ~2 KB, which would drop the tail
        // (including the configuration-guide signpost). Keep the whole thing under 2048 UTF-8 bytes.
        Encoding.UTF8.GetByteCount(ServerInstructions.Text).ShouldBeLessThanOrEqualTo(2048);
    }

    [Fact]
    public void Text_SignpostsTheConfigurationGuideResource()
    {
        // The detailed config model lives in an on-demand resource; the instructions only point at it.
        // Guard the URI so the signpost can't be silently dropped, breaking on-demand discovery.
        ServerInstructions.Text.ShouldContain(ResharperResources.ConfigurationGuideUri);
    }
}