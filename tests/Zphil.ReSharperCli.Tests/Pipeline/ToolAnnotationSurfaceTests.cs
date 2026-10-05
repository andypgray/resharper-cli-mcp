using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Pipeline;

/// <summary>
///     The surface every MCP client and directory listing renders for this server — the tool names, their
///     human titles, and the four behavior hints — read back off a real <c>tools/list</c> and pinned as
///     spec, so a change to what the server advertises is a deliberate edit here rather than a side effect
///     of one somewhere else. The name set is a ratchet: a fourth tool joins this table or it does not ship.
///     <para>
///         The title is pinned on <c>Tool.Title</c> alone. MCP SDK 2.2.0 writes the
///         <c>[McpServerTool(Title = …)]</c> value into <c>Tool.Annotations.Title</c> as well, so both
///         carry it today and asserting both would pin one fact twice.
///     </para>
/// </summary>
public sealed class ToolAnnotationSurfaceTests(AdvertisedToolsFixture advertised)
    : IClassFixture<AdvertisedToolsFixture>
{
    /// <summary>
    ///     One row per advertised tool: its name, its human title, then <c>ReadOnly</c>, <c>Destructive</c>,
    ///     <c>Idempotent</c> and <c>OpenWorld</c> in the order the <c>[McpServerTool]</c> attribute declares
    ///     them. Read-only and destructive are the two a client gates auto-approval on.
    /// </summary>
    public static TheoryData<string, string, bool, bool, bool, bool> AdvertisedTools =>
        new()
        {
            // Read-only survives the report parameter on purpose: a run already creates and deletes a temp
            // directory for jb's SARIF, the delta is one file surviving in a directory this server owns, and at
            // the default nothing is written at all.
            { "resharper_inspect", "ReSharper Inspect Code", true, false, true, false },
            { "resharper_cleanup", "ReSharper Cleanup Code", false, true, true, false },
            { "resharper_reset_cache", "ReSharper Reset Cache", false, true, true, false }
        };

    [Fact]
    public void ListTools_AdvertisesExactlyThePinnedTools()
    {
        // Act — the whole set, not containment: a tool this table does not name is as much a change to the
        // published surface as a missing one, and CoercingToolRegistration discovers tools by attribute
        // rather than from a list, so nothing else makes adding one a decision. Sorted rather than order-
        // insensitive because the order tools/list returns is the discovery order, which is not a contract.
        IEnumerable<string> names = advertised.Tools
            .Select(tool => tool.Name)
            .Order(StringComparer.Ordinal);

        // Assert
        names.ShouldBe(["resharper_cleanup", "resharper_inspect", "resharper_reset_cache"]);
    }

    [Theory]
    [MemberData(nameof(AdvertisedTools))]
    public void ListTools_AdvertisedTool_CarriesItsPinnedTitleAndHints(
        string name,
        string title,
        bool readOnly,
        bool destructive,
        bool idempotent,
        bool openWorld)
    {
        // Act — the raw protocol DTO rather than the client wrapper, because that object is what is
        // serialized onto the wire and therefore exactly what a directory listing renders.
        Tool tool = advertised.Tools.Single(advertisedTool => advertisedTool.Name == name).ProtocolTool;

        // Assert — the four hints travel together in one annotations object, so a null one loses all four.
        tool.Title.ShouldBe(title);

        ToolAnnotations? annotations = tool.Annotations;
        annotations.ShouldNotBeNull();
        annotations.ReadOnlyHint.ShouldBe(readOnly);
        annotations.DestructiveHint.ShouldBe(destructive);
        annotations.IdempotentHint.ShouldBe(idempotent);
        annotations.OpenWorldHint.ShouldBe(openWorld);
    }
}