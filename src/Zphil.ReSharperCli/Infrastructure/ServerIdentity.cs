using ModelContextProtocol.Protocol;

namespace Zphil.ReSharperCli.Infrastructure;

/// <summary>
///     The <see cref="Implementation" /> this server sends as <c>serverInfo</c> on <c>initialize</c>: name,
///     display title, version and icon. It lives here rather than at the composition root because the
///     integration harness builds a host of its own, and a second literal there would let the handshake a
///     test asserts on drift from the handshake a client actually gets.
/// </summary>
internal static class ServerIdentity
{
    /// <summary>The icon's media type. PNG is the one tier the icons spec requires every client to render.</summary>
    private const string IconMimeType = "image/png";

    /// <summary>
    ///     The embedded render's pixel dimensions, in the <c>WxH</c> form the icons spec specifies.
    /// </summary>
    private const string IconSize = "128x128";

    /// <summary>
    ///     The icon as a <c>data:</c> URI. The icons spec allows an HTTP(S) URL or a <c>data:</c> URI, and a
    ///     stdio server is the case that makes the choice: it listens on no authority, so a remote URL has no
    ///     domain for a client to match it against, and it has no install path a manifest could point at that
    ///     survives a tool install, a <c>dnx</c> cache and a container image alike. The bytes travel in the
    ///     handshake instead — about 2.5 KB of base64, once per session, and no fetch.
    /// </summary>
    private static readonly string IconDataUri = BuildIconDataUri();

    /// <summary>
    ///     Builds the identity. A fresh instance per call: <see cref="Implementation" /> is mutable, and a
    ///     shared one would be a single object handed to every host in a process.
    /// </summary>
    internal static Implementation Create()
    {
        return new Implementation
        {
            Name = "resharper-cli-mcp",
            Title = "ReSharper CLI Tools (unofficial)",
            Version = ServerVersion.SemVer,
            Icons =
            [
                new Icon
                {
                    // No theme: the mark is drawn on its own opaque tile, so it reads on either background.
                    Source = IconDataUri,
                    MimeType = IconMimeType,
                    Sizes = [IconSize]
                }
            ]
        };
    }

    /// <summary>Reads the embedded PNG and spells it as a base64 <c>data:</c> URI.</summary>
    private static string BuildIconDataUri()
    {
        byte[] png = EmbeddedResource.LoadBytes("Zphil.ReSharperCli.icon-128.png");

        return $"data:{IconMimeType};base64,{Convert.ToBase64String(png)}";
    }
}