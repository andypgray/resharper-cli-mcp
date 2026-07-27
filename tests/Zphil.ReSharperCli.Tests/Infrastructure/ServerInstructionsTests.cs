using System.Text;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Infrastructure;
using Zphil.ReSharperCli.Resources;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Tests.Infrastructure;

/// <summary>
///     Guards the embedded <c>server-instructions.md</c> resource. A rename of the file or its manifest
///     resource id would otherwise surface only as a runtime failure when a client connects; these
///     load-time assertions turn it into a test failure instead. They also pin the resident-cost budget:
///     instructions ride verbatim in every session's system prompt whether or not a tool is ever called,
///     while tool schemas and resources load on demand, so anything that can live in a schema or a guide
///     must not live here.
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
    public void Text_StaysUnderClaudeCodeTruncationCap()
    {
        // Claude Code silently truncates server instructions past ~2 KB, which would drop the tail
        // (including the configuration-guide signpost). Keep the whole thing under 2048 UTF-8 bytes.
        Encoding.UTF8.GetByteCount(ServerInstructions.Text).ShouldBeLessThanOrEqualTo(2048);
    }

    [Fact]
    public void Text_StaysWithinTheResidentTokenBudget()
    {
        // The design budget, well below the client truncation cap above: this text is resident in every
        // session that connects the server, including the majority that never call a tool. New prose
        // belongs in a guide resource (resharper://guides/configuration or /setup) or in a parameter
        // [Description], both of which load only on demand. Only cross-tool routing that no single schema
        // can state earns a place here.
        Encoding.UTF8.GetByteCount(ServerInstructions.Text).ShouldBeLessThanOrEqualTo(1200);
    }

    [Fact]
    public void Text_SignpostsBothGuideResources()
    {
        // The detailed config and setup models live in on-demand resources; the instructions only point at
        // them. Guard both URIs so a signpost can't be silently dropped, breaking on-demand discovery.
        ServerInstructions.Text.ShouldContain(ResharperResources.ConfigurationGuideUri);
        ServerInstructions.Text.ShouldContain(ResharperResources.SetupGuideUri);
    }

    [Fact]
    public void Text_CarriesNoLegalDisclaimer()
    {
        // The unofficial-wrapper notice is required in the NuGet description, the README's first paragraph,
        // and .mcp/server.json (see RespectfulWrappingTests) — not in the always-resident instructions,
        // where it costs every session tokens no agent can act on. It also survives in both guide resources,
        // the derive_style_guide prompt, and the server's own Title, negotiated on initialize.
        ServerInstructions.Text.ShouldNotContain("affiliated");
    }

    [Fact]
    public void Text_PublishesNoSeverityEnumValues()
    {
        // severity's legal values already travel in the tool schema's enum array, which the client fetches
        // on demand. Restating them here would pay for them in every session. Ordinal comparison, so
        // lowercase prose ("raise the severity") cannot false-trip this guard.
        foreach (string value in Enum.GetNames<InspectSeverity>())
            ServerInstructions.Text.Contains(value, StringComparison.Ordinal)
                .ShouldBeFalse($"Instructions restate the severity value \"{value}\"; the schema enum already carries it.");
    }
}