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
///     Pins <see cref="ResharperResources" />' two MCP resources, <c>resharper://guides/configuration</c> and
///     <c>resharper://guides/setup</c>.
/// </summary>
/// <remarks>
///     Reading each guide back as the same text the resource method returns is also the load-time guard: a
///     rename of an embedded <c>.md</c> or its manifest id fails the read rather than surfacing only when a
///     client reads it. The anchors are asserted against each guide directly, on stable phrases rather than the
///     whole blob, so wording can evolve while the two-axes/editorconfig/DotSettings spec and the setup facts
///     cannot silently drift.
/// </remarks>
public sealed class ResharperResourcesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ResharperResources.ConfigurationGuideUri, ResharperResources.ConfigurationGuideName)]
    [InlineData(ResharperResources.SetupGuideUri, ResharperResources.SetupGuideName)]
    public async Task ListResources_AdvertisesTheGuideAsADirectResource(string uri, string name)
    {
        // Arrange
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Act — resources/list carries only direct resources; a URI with no {param} must land here.
        IList<McpClientResource> resources = await harness.Client.ListResourcesAsync(cancellationToken: Ct);

        // Assert
        resources.Select(resource => resource.Uri).ShouldContain(uri);
        resources.Select(resource => resource.Name).ShouldContain(name);
    }

    [Theory]
    [InlineData(ResharperResources.ConfigurationGuideUri)]
    [InlineData(ResharperResources.SetupGuideUri)]
    public async Task ReadResource_ServesTheGuideAsOneMarkdownText(string uri)
    {
        // Arrange
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        string expected = GuideAt(uri);

        // Act
        ReadResourceResult result = await harness.Client.ReadResourceAsync(uri, cancellationToken: Ct);

        // Assert — a string-returning resource method maps to one TextResourceContents.
        var contents = result.Contents.ShouldHaveSingleItem().ShouldBeOfType<TextResourceContents>();
        contents.MimeType.ShouldBe("text/markdown");
        contents.Text.ShouldBe(expected);
    }

    [Fact]
    public void ConfigurationGuide_CarriesItsLoadBearingAnchors()
    {
        // Act
        string text = ResharperResources.ConfigurationGuide();

        // Assert
        text.ShouldContain("DO_NOT_SHOW"); // inspect-axis suppression that does NOT stop cleanup
        text.ShouldContain("positional"); // the binary argument-style gotcha with no leave-alone value
        text.ShouldContain(".editorconfig"); // jb auto-honors it from the tree
        text.ShouldContain("InspectionSeverities"); // the DotSettings severity key shape
        text.ShouldContain("resharper_cleanup"); // the style axis
        text.ShouldContain(ResharperResources.SetupGuideUri); // the onward cross-link to the setup guide
        text.ShouldContain("@formatter:off"); // the only lever measured to survive a formatting revert
        text.ShouldContain("extracting it to a local"); // the other one: change the shape, nothing to revert
    }

    [Fact]
    public void SetupGuide_CarriesItsLoadBearingAnchors()
    {
        // Act
        string text = ResharperResources.SetupGuide();

        // Assert
        text.ShouldContain("JetBrains.ReSharper.GlobalTools"); // the install command for the missing jb
        text.ShouldContain("without a restart"); // a jb updated in place is picked up by the next call
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
        text.ShouldContain("`jb` does not restore them"); // and the cheaper origin, which a reset makes worse
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

    // The full set the server reads, hardcoded to match CLAUDE.md's Identity table: the product spells these
    // in several places with no single list to reflect over, so a variable added to the product needs a row
    // here too. MAX_MCP_OUTPUT_TOKENS is set by the MCP client rather than the user, and still needs
    // documenting because it is what caps a truncated result.
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
        // Assert — the always-resident server instructions name no variable, so the setup guide is each one's
        // only agent-facing home, and a variable missing there is invisible to every agent.
        ResharperResources.SetupGuide().ShouldContain(variable);
    }

    /// <summary>The text the resource method behind <paramref name="uri" /> returns when called directly.</summary>
    private static string GuideAt(string uri)
    {
        return uri switch
        {
            ResharperResources.ConfigurationGuideUri => ResharperResources.ConfigurationGuide(),
            ResharperResources.SetupGuideUri => ResharperResources.SetupGuide(),
            _ => throw new ArgumentOutOfRangeException(nameof(uri), uri, "No guide is served at this URI.")
        };
    }
}