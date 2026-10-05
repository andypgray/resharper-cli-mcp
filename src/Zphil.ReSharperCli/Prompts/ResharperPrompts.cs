using System.ComponentModel;
using ModelContextProtocol.Server;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Prompts;

/// <summary>
///     The MCP prompt surface: one prompt, <c>derive_style_guide</c>, whose body is the embedded
///     <c>derive-style-guide.md</c> recipe.
/// </summary>
/// <remarks>
///     The class is deliberately non-static: <c>WithPrompts&lt;ResharperPrompts&gt;()</c> takes it as a
///     type argument, and static classes cannot be type arguments. The prompt method is <c>static</c>, so
///     no instance is ever constructed. A <c>string</c>-returning method maps to a single
///     <c>Role.User</c> <c>PromptMessage</c> carrying the markdown body.
/// </remarks>
[McpServerPromptType]
internal sealed class ResharperPrompts
{
    internal const string DeriveStyleGuideName = "derive_style_guide";

    private const string DeriveStyleGuideDescription =
        "Derive an intentional ReSharper/editorconfig style guide from an existing (legacy) C# codebase, "
        + "reconcile it with tooling already present (StyleCop, analyzers), and validate it with the "
        + "resharper_inspect loop. The server does not infer settings; this recipe guides you to.";

    [McpServerPrompt(Name = DeriveStyleGuideName, Title = "Derive a ReSharper style guide")]
    [Description(DeriveStyleGuideDescription)]
    internal static string DeriveStyleGuide()
    {
        return EmbeddedResource.LoadText("Zphil.ReSharperCli.Prompts.derive-style-guide.md");
    }
}