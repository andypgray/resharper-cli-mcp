namespace Zphil.ReSharperCli.Infrastructure;

/// <summary>
///     Loads the embedded <c>server-instructions.md</c> resource for use as the MCP server's
///     <c>ServerInstructions</c> (surfaced to clients on <c>initialize</c>).
/// </summary>
internal static class ServerInstructions
{
    internal static readonly string Text =
        EmbeddedResourceText.Load("Zphil.ReSharperCli.server-instructions.md");
}