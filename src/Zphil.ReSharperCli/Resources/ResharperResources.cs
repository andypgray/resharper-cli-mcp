using System.ComponentModel;
using ModelContextProtocol.Server;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Resources;

/// <summary>
///     Defines the MCP resource surface: on-demand guides, each answering a single routing condition so a pull
///     for one never drags in another.
/// </summary>
/// <remarks>
///     Non-static with <c>static</c> resource methods, for the reason given on
///     <see cref="Prompts.ResharperPrompts" />. The guides live on this one class so the registration stays a
///     single type argument. No URI template carries a <c>{parameter}</c>, so the SDK registers each as a
///     direct resource (surfaced by <c>resources/list</c>), not a template. A <c>string</c> return maps to a
///     single <c>TextResourceContents</c>.
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
        + "the run cap (this server's own, and raisable) and why calls against one solution queue, how "
        + "MAX_MCP_OUTPUT_TOKENS caps output, how a reduced result differs from a truncated one and how to "
        + "get the complete itemised findings written to a file instead, the "
        + "JB_SETTINGS_PATH, JB_CACHE_HOME, JB_EXTENSIONS, JB_EXTENSION_SOURCE, "
        + "RESHARPER_MCP_TIMEOUT_SECS, RESHARPER_MCP_PREWARM, RESHARPER_MCP_CLEANUP_FILTER, and "
        + "RESHARPER_MCP_LOG_LEVEL variables, "
        + "and where logs go. Load this when a call cannot find jb or the solution, times out, reports "
        + "another run already in flight, comes back shortened, or is slower than the queue explains "
        + "alongside a jb run outside this server — which is also how an unexpected extra cache "
        + "generation appears.";

    [McpServerResource(
        UriTemplate = ConfigurationGuideUri,
        Name = ConfigurationGuideName,
        Title = "Configuring ReSharper",
        MimeType = "text/markdown")]
    [Description(ConfigurationGuideDescription)]
    internal static string ConfigurationGuide()
    {
        return EmbeddedResource.LoadText("Zphil.ReSharperCli.Resources.configuration-guide.md");
    }

    [McpServerResource(
        UriTemplate = SetupGuideUri,
        Name = SetupGuideName,
        Title = "Running the ReSharper CLI server",
        MimeType = "text/markdown")]
    [Description(SetupGuideDescription)]
    internal static string SetupGuide()
    {
        return EmbeddedResource.LoadText("Zphil.ReSharperCli.Resources.setup-guide.md");
    }
}