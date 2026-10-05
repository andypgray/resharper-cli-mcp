using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Prompts;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Prompts;

/// <summary>
///     Pins the <c>derive_style_guide</c> MCP prompt: over the in-memory client/server harness it is
///     advertised in <c>prompts/list</c> and <c>prompts/get</c> returns a single user message carrying the
///     embedded recipe verbatim; the recipe itself carries its load-bearing commitments and reference links.
///     Assertions target a few stable anchor phrases, not the whole blob, so wording can evolve while the
///     honesty/editorconfig/inspect-loop spec cannot silently drift.
/// </summary>
public sealed class StyleGuidePromptTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListPrompts_AdvertisesDeriveStyleGuide()
    {
        // Arrange
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Act
        IList<McpClientPrompt> prompts = await harness.Client.ListPromptsAsync(cancellationToken: Ct);

        // Assert — registering the prompt advertises the capability and lists it by name.
        prompts.Select(prompt => prompt.Name).ShouldContain(ResharperPrompts.DeriveStyleGuideName);
    }

    [Fact]
    public async Task GetPrompt_ReturnsOneUserTextMessage()
    {
        // Arrange
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Act
        GetPromptResult result = await harness.Client.GetPromptAsync(
            ResharperPrompts.DeriveStyleGuideName, cancellationToken: Ct);

        // Assert — a string-returning prompt method maps to one Role.User text message, and the wire carries
        // the embedded recipe unaltered, so the anchor fact below holds for what a client receives.
        PromptMessage message = result.Messages.ShouldHaveSingleItem();
        message.Role.ShouldBe(Role.User);
        message.Content.ShouldBeOfType<TextContentBlock>().Text.ShouldBe(ResharperPrompts.DeriveStyleGuide());
    }

    [Fact]
    public void DeriveStyleGuide_CarriesItsCommitmentsAndReferenceLinks()
    {
        // Act — loading the embedded recipe also guards a rename of the .md or its manifest id, which would
        // otherwise surface only when a client calls prompts/get.
        string text = ResharperPrompts.DeriveStyleGuide();

        // Assert — the commitments.
        text.ShouldContain("does not infer"); // honesty
        text.ShouldContain("not affiliated with or endorsed by JetBrains"); // respectful wrapping
        text.ShouldContain("Detect Code Style Settings"); // prefer the official IDE detector
        text.ShouldContain(".editorconfig"); // editorconfig-first
        text.ShouldContain("resharper_inspect"); // the validation loop
        text.ShouldContain("do not guess"); // resolve conflicts with the user

        // Assert — the executing agent needs the real specs; guard the links so they aren't dropped.
        text.ShouldContain("EditorConfig_Properties.html");
        text.ShouldContain("stylecop.schema.json");
        text.ShouldContain("InspectCode.html");
    }
}