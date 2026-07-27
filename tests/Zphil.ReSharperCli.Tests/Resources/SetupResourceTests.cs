using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Resources;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Resources;

/// <summary>
///     Pins the <c>resharper://guides/setup</c> MCP resource end to end over the in-memory client/server
///     harness, mirroring <see cref="ConfigurationResourceTests" />: its URI template carries no
///     <c>{parameter}</c>, so it must be advertised as a <em>direct</em> resource in <c>resources/list</c>,
///     and <c>resources/read</c> must return the markdown setup guide. Assertions target stable anchor
///     phrases rather than the whole blob. The environment-variable fact is the load-bearing one: since the
///     always-resident server instructions no longer name any variable, this guide is their only
///     agent-facing home, and a variable missing here is invisible to every agent.
/// </summary>
public sealed class SetupResourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListResources_AdvertisesSetupGuideAsDirectResource()
    {
        // Arrange
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Act — resources/list carries only direct resources; a URI with no {param} must land here.
        var resources = await harness.Client.ListResourcesAsync(cancellationToken: Ct);

        // Assert
        resources.Select(resource => resource.Uri).ShouldContain(ResharperResources.SetupGuideUri);
        resources.Select(resource => resource.Name).ShouldContain(ResharperResources.SetupGuideName);
    }

    [Fact]
    public async Task ReadResource_ReturnsMarkdownCarryingLoadBearingAnchors()
    {
        // Arrange
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Act
        ReadResourceResult result = await harness.Client.ReadResourceAsync(
            ResharperResources.SetupGuideUri, cancellationToken: Ct);

        // Assert — a string-returning resource method maps to one TextResourceContents.
        var contents = result.Contents.ShouldHaveSingleItem().ShouldBeOfType<TextResourceContents>();
        contents.MimeType.ShouldBe("text/markdown");
        string text = contents.Text;
        text.ShouldContain("JetBrains.ReSharper.GlobalTools"); // the install command for the missing jb
        text.ShouldContain("no parent walk"); // solution discovery is top-level only
        text.ShouldContain("5 minutes"); // the per-run cap behind most timeouts
        text.ShouldContain("25,000"); // the output cap when the client sets no budget
        text.ShouldContain(ResharperResources.ConfigurationGuideUri); // the onward cross-link
    }

    // The full set the server reads, hardcoded rather than reflected so that adding a variable to the
    // product without documenting it fails here. Matches CLAUDE.md's Identity table; MAX_MCP_OUTPUT_TOKENS
    // is set by the MCP client rather than the user, and still needs documenting because it is what caps a
    // truncated result.
    [Theory]
    [InlineData("JB_SOLUTION_PATH")]
    [InlineData("JB_SETTINGS_PATH")]
    [InlineData("JB_CACHE_HOME")]
    [InlineData("JB_EXTENSIONS")]
    [InlineData("JB_EXTENSION_SOURCE")]
    [InlineData("RESHARPER_MCP_LOG_LEVEL")]
    [InlineData("MAX_MCP_OUTPUT_TOKENS")]
    public void SetupGuide_DocumentsEveryEnvironmentVariable(string variable)
    {
        // Assert — the instructions dropped the variable names, so an undocumented variable is unreachable.
        ResharperResources.SetupGuide().ShouldContain(variable);
    }

    [Fact]
    public void SetupGuide_LoadsEmbeddedResource_NonTrivial()
    {
        // A rename of the .md or its manifest id (LogicalName is only checked at runtime, so the build
        // stays green) would otherwise surface only when a client reads the resource.
        ResharperResources.SetupGuide().Length.ShouldBeGreaterThan(500);
    }
}