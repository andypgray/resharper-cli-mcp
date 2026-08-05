using System.ComponentModel;
using ModelContextProtocol.Server;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Resources;

/// <summary>
///     The MCP resource surface: two on-demand guides, each answering a single routing condition so a pull
///     for one never drags in the other. <c>resharper://guides/configuration</c> serves the embedded
///     <c>configuration-guide.md</c> — the two-axes model an agent needs before changing what ReSharper
///     enforces (inspection severities drive <c>resharper_inspect</c>; the cleanup profile drives
///     <c>resharper_cleanup</c>, and the two never share a switch). <c>resharper://guides/setup</c> serves
///     <c>setup-guide.md</c> — how <c>jb</c> and the solution are discovered, the cold-cache slowness, the
///     5-minute run cap and the queue that keeps concurrent calls off each other's cache, how output is
///     shortened to fit the budget, the environment variables, and where logs go. Both bodies load on demand,
///     keeping the always-resident server instructions short.
/// </summary>
/// <remarks>
///     Mirrors <see cref="Prompts.ResharperPrompts" />: the class is non-static because
///     <c>WithResources&lt;ResharperResources&gt;()</c> takes it as a type argument, while the resource
///     methods are <c>static</c>, so no instance is ever constructed. Both live on this one class so that
///     registration call site stays a single type argument. Neither URI template carries a
///     <c>{parameter}</c>, so the SDK registers both as <em>direct</em> resources (surfaced by
///     <c>resources/list</c>), not templates. A <c>string</c> return maps to a single
///     <c>TextResourceContents</c>.
/// </remarks>
[McpServerResourceType]
internal sealed class ResharperResources
{
    internal const string ConfigurationGuideUri = "resharper://guides/configuration";
    internal const string ConfigurationGuideName = "resharper_configuration_guide";

    internal const string SetupGuideUri = "resharper://guides/setup";
    internal const string SetupGuideName = "resharper_setup_guide";

    private const string ConfigurationGuideDescription =
        "How ReSharper configuration works for this server: inspection severities drive resharper_inspect "
        + "while the cleanup profile drives resharper_cleanup (they never share a switch), how to protect a "
        + "deliberate style from cleanup, where settings and .editorconfig are read from, and the DotSettings "
        + "key shapes. Load this before changing what ReSharper enforces. It does not cover running the "
        + "server: for a call that cannot find the solution, times out, or comes back shortened, read "
        + SetupGuideUri + " instead.";

    private const string SetupGuideDescription =
        "How to run this server and diagnose a failing call: installing and locating jb (PATH, then "
        + "~/.dotnet/tools), which solution a call runs against (the solutionPath argument, JB_SOLUTION_PATH, "
        + "then a single .sln/.slnx in the working directory with no parent walk), why the first call is slow, "
        + "the 5-minute run cap and why calls against one solution queue, how MAX_MCP_OUTPUT_TOKENS caps "
        + "output and how a reduced result differs from a truncated one, the JB_SETTINGS_PATH, JB_CACHE_HOME, "
        + "JB_EXTENSIONS, JB_EXTENSION_SOURCE, RESHARPER_MCP_PREWARM, and RESHARPER_MCP_LOG_LEVEL variables, "
        + "and where logs go. Load this when a call cannot find jb or the solution, times out, reports "
        + "another run already in flight, or comes back shortened.";

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

    [McpServerResource(
        UriTemplate = SetupGuideUri,
        Name = SetupGuideName,
        Title = "Running the ReSharper CLI server",
        MimeType = "text/markdown")]
    [Description(SetupGuideDescription)]
    internal static string SetupGuide()
    {
        return EmbeddedResourceText.Load("Zphil.ReSharperCli.Resources.setup-guide.md");
    }
}