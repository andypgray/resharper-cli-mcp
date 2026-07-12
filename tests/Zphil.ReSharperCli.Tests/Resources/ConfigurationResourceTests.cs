using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Resources;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Resources;

/// <summary>
///     Pins the <c>resharper://guides/configuration</c> MCP resource end to end over the in-memory
///     client/server harness: because its URI template carries no <c>{parameter}</c>, it must be advertised
///     as a <em>direct</em> resource in <c>resources/list</c> (not only <c>resources/templates/list</c>), and
///     <c>resources/read</c> must return the markdown config guide whose body carries the load-bearing
///     anchors an agent needs. Assertions target stable anchor phrases, not the whole blob, so wording can
///     evolve while the two-axes/editorconfig/DotSettings spec cannot silently drift. A load-time guard
///     mirrors <c>ServerInstructionsTests</c> for the embedded resource.
/// </summary>
public sealed class ConfigurationResourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListResources_AdvertisesConfigurationGuideAsDirectResource()
    {
        // Arrange
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Act — resources/list carries only direct resources; a URI with no {param} must land here.
        var resources = await harness.Client.ListResourcesAsync(cancellationToken: Ct);

        // Assert
        resources.Select(resource => resource.Uri).ShouldContain(ResharperResources.ConfigurationGuideUri);
        resources.Select(resource => resource.Name).ShouldContain(ResharperResources.ConfigurationGuideName);
    }

    [Fact]
    public async Task ReadResource_ReturnsMarkdownCarryingLoadBearingAnchors()
    {
        // Arrange
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Act
        ReadResourceResult result = await harness.Client.ReadResourceAsync(
            ResharperResources.ConfigurationGuideUri, cancellationToken: Ct);

        // Assert — a string-returning resource method maps to one TextResourceContents.
        var contents = result.Contents.ShouldHaveSingleItem().ShouldBeOfType<TextResourceContents>();
        contents.MimeType.ShouldBe("text/markdown");
        string text = contents.Text;
        text.ShouldContain("DO_NOT_SHOW"); // inspect-axis suppression that does NOT stop cleanup
        text.ShouldContain("positional"); // the binary argument-style gotcha with no leave-alone value
        text.ShouldContain(".editorconfig"); // jb auto-honors it from the tree
        text.ShouldContain("InspectionSeverities"); // the DotSettings severity key shape
        text.ShouldContain("resharper_cleanup"); // the style axis
    }

    [Fact]
    public void ConfigurationGuide_LoadsEmbeddedResource_NonTrivial()
    {
        // A rename of the .md or its manifest id would otherwise surface only when a client reads the
        // resource; this load-time assertion turns it into a test failure instead.
        ResharperResources.ConfigurationGuide().Length.ShouldBeGreaterThan(500);
    }
}