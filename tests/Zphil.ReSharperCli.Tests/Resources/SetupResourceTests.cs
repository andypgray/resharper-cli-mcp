using System.Globalization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Infrastructure;
using Zphil.ReSharperCli.Resources;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestSupport;
using Zphil.ReSharperCli.Tools;

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
        IList<McpClientResource> resources = await harness.Client.ListResourcesAsync(cancellationToken: Ct);

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

        // The run-cap numbers, derived from their owner rather than restated: the guide is the caps' only
        // agent-facing home, so a change to JbRunTimeout that skips the guide must fail here, not ship a
        // document asserting the wrong cap.
        text.ShouldContain($"capped at **{(int)JbRunTimeout.Default.TotalMinutes} minutes**");
        text.ShouldContain($"default `{(int)JbRunTimeout.Default.TotalSeconds}`");
        text.ShouldContain(
            $"Clamped to {(int)JbRunTimeout.Floor.TotalSeconds}…{JbRunTimeout.Ceiling.TotalSeconds.ToString("N0", CultureInfo.InvariantCulture)}");

        text.ShouldContain("queue"); // why a concurrent call waits rather than forking a cold cache
        text.ShouldContain("25,000"); // the output cap when the client sets no budget
        text.ShouldContain("DETAIL REDUCED"); // the marker an agent actually sees on an over-budget result

        // The other way out of the budget, and the retention that decides how long the path it hands back
        // stays good — derived from its owner, so lengthening the window cannot leave the guide behind.
        text.ShouldContain("`report=Markdown`");
        text.ShouldContain($"{(int)InspectReportWriter.RetentionPeriod.TotalDays} days old");

        // The other half of that pair: asking for a level rather than overflowing into one, the cap-not-pin
        // rule, and the composition that answers the survey-a-legacy-solution case in one call.
        text.ShouldContain("pass `detail`");
        text.ShouldContain("Rendered at the requested detail level"); // the note's own lead, quoted
        text.ShouldContain("`detail=Minimal report=Markdown`");

        text.ShouldContain("CSharpErrors"); // the rule that identifies a stale solution-wide index
        text.ShouldContain(ResharperTools.ResetCacheToolName); // and the tool that clears it
        text.ShouldContain("worktree"); // the always-cold case, and the only place the seeding is described
        text.ShouldContain("Running `jb` yourself"); // how far the queue reaches: a jb the server never spawned is outside it

        // The other way a fork appears — a client killing the server outright — and the three values the
        // startup line can report for it, derived from their owner so a renamed guarantee cannot leave the
        // guide describing a field nobody will find in a log.
        text.ShouldContain(ChildProcessLifetime.KillOnJobClose);
        text.ShouldContain(ChildProcessLifetime.ParentDeathSignalled);
        text.ShouldContain("orphan guard");

        text.ShouldContain(ResharperResources.ConfigurationGuideUri); // the onward cross-link

        // The log section, which is the only place the level policy is stated for a reader: that raising to
        // Information buys the cache story, and that the frameworks are held down so it is findable.
        text.ShouldContain("held at `Warning`");
        text.ShouldContain(RunIdScope.OutsideARun); // the run column on a line belonging to no run
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
    [InlineData("RESHARPER_MCP_TIMEOUT_SECS")]
    [InlineData("RESHARPER_MCP_PREWARM")]
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