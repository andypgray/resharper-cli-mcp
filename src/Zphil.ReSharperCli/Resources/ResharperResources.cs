using System.ComponentModel;
using ModelContextProtocol.Server;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Resources;

/// <summary>
///     The MCP resource surface. One resource, <c>resharper://guides/configuration</c>, whose body is the
///     embedded <c>configuration-guide.md</c>: the two-axes model an agent needs before changing what
///     ReSharper enforces (inspection severities drive <c>resharper_inspect</c>; the cleanup profile drives
///     <c>resharper_cleanup</c>, and the two never share a switch). The detail lives here, loaded on demand,
///     rather than in the always-loaded server instructions.
/// </summary>
/// <remarks>
///     Mirrors <see cref="Prompts.ResharperPrompts" />: the class is non-static because
///     <c>WithResources&lt;ResharperResources&gt;()</c> takes it as a type argument, while the resource
///     method is <c>static</c>, so no instance is ever constructed. The URI template carries no
///     <c>{parameter}</c>, so the SDK registers it as a <em>direct</em> resource (surfaced by
///     <c>resources/list</c>), not a template. A <c>string</c> return maps to a single
///     <c>TextResourceContents</c>.
/// </remarks>
[McpServerResourceType]
internal sealed class ResharperResources
{
    internal const string ConfigurationGuideUri = "resharper://guides/configuration";
    internal const string ConfigurationGuideName = "resharper_configuration_guide";

    private const string ConfigurationGuideDescription =
        "How ReSharper configuration works for this server: inspection severities drive resharper_inspect "
        + "while the cleanup profile drives resharper_cleanup (they never share a switch), how to protect a "
        + "deliberate style from cleanup, where settings and .editorconfig are read from, and the DotSettings "
        + "key shapes. Load this before changing what ReSharper enforces.";

    [McpServerResource(
        UriTemplate = ConfigurationGuideUri,
        Name = ConfigurationGuideName,
        Title = "Configuring ReSharper",
        MimeType = "text/markdown")]
    [Description(ConfigurationGuideDescription)]
    internal static string ConfigurationGuide()
    {
        return EmbeddedResourceText.Load("Zphil.ReSharperCli.Resources.configuration-guide.md");
    }
}