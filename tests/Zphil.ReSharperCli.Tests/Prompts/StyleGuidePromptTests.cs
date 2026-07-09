using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Prompts;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Prompts;

/// <summary>
///     Pins the <c>derive_style_guide</c> MCP prompt end to end over the in-memory client/server harness:
///     it is advertised in <c>prompts/list</c> and <c>prompts/get</c> returns a single user message whose
///     body carries the recipe's load-bearing commitments. Assertions target a few stable anchor phrases,
///     not the whole blob, so wording can evolve while the honesty/editorconfig/inspect-loop spec cannot
///     silently drift. A load-time guard mirrors <c>ServerInstructionsTests</c> for the embedded resource.
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
        var prompts = await harness.Client.ListPromptsAsync(cancellationToken: Ct);

        // Assert — registering the prompt advertises the capability and lists it by name.
        prompts.Select(prompt => prompt.Name).ShouldContain(ResharperPrompts.DeriveStyleGuideName);
    }

    [Fact]
    public async Task GetPrompt_ReturnsSingleUserMessageCarryingLoadBearingCommitments()
    {
        // Arrange
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Act
        GetPromptResult result = await harness.Client.GetPromptAsync(
            ResharperPrompts.DeriveStyleGuideName, cancellationToken: Ct);

        // Assert — a string-returning prompt method maps to one Role.User text message.
        PromptMessage message = result.Messages.ShouldHaveSingleItem();
        message.Role.ShouldBe(Role.User);
        string text = message.Content.ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("does not infer"); // honesty
        text.ShouldContain("not affiliated with or endorsed by JetBrains"); // respectful wrapping
        text.ShouldContain("Detect Code Style Settings"); // prefer the official IDE detector
        text.ShouldContain(".editorconfig"); // editorconfig-first
        text.ShouldContain("resharper_inspect"); // the validation loop
        text.ShouldContain("do not guess"); // resolve conflicts with the user
    }

    [Fact]
    public async Task GetPrompt_EmbedsAuthoritativeReferenceLinks()
    {
        // Arrange
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Act
        GetPromptResult result = await harness.Client.GetPromptAsync(
            ResharperPrompts.DeriveStyleGuideName, cancellationToken: Ct);

        // Assert — the executing agent needs the real specs; guard the links so they aren't dropped.
        string text = ((TextContentBlock)result.Messages[0].Content).Text;
        text.ShouldContain("EditorConfig_Properties.html");
        text.ShouldContain("stylecop.schema.json");
        text.ShouldContain("InspectCode.html");
    }

    [Fact]
    public void DeriveStyleGuide_LoadsEmbeddedRecipe_NonTrivial()
    {
        // A rename of the .md or its manifest id would otherwise surface only when a client calls
        // prompts/get; this load-time assertion turns it into a test failure instead.
        ResharperPrompts.DeriveStyleGuide().Length.ShouldBeGreaterThan(500);
    }
}