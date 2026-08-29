using System.Reflection;

namespace Zphil.ReSharperCli.Infrastructure;

/// <summary>
///     Reads a resource embedded in this assembly by its manifest (logical) name, as text or as bytes.
///     Shared by every consumer that ships a file inside the assembly and reads it back at load time —
///     the server instructions (<see cref="ServerInstructions" />), the MCP prompt and resource bodies,
///     and the handshake icon (<see cref="ServerIdentity" />) — so a renamed file or drifted resource id
///     fails loudly and identically in all of them.
/// </summary>
internal static class EmbeddedResource
{
    /// <summary>
    ///     Loads the embedded resource named <paramref name="logicalName" /> and returns its full text,
    ///     decoded as UTF-8.
    /// </summary>
    /// <param name="logicalName">
    ///     The manifest resource id — the assembly-qualified logical name, for example
    ///     <c>Zphil.ReSharperCli.server-instructions.md</c>.
    /// </param>
    /// <exception cref="InvalidOperationException">No resource with that id is embedded in the assembly.</exception>
    internal static string LoadText(string logicalName)
    {
        using Stream stream = Open(logicalName);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    ///     Loads the embedded resource named <paramref name="logicalName" /> and returns its raw bytes,
    ///     for the resources that are not text.
    /// </summary>
    /// <param name="logicalName">
    ///     The manifest resource id — the assembly-qualified logical name, for example
    ///     <c>Zphil.ReSharperCli.icon-128.png</c>.
    /// </param>
    /// <exception cref="InvalidOperationException">No resource with that id is embedded in the assembly.</exception>
    internal static byte[] LoadBytes(string logicalName)
    {
        using Stream stream = Open(logicalName);
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Opens the named manifest resource, or throws naming the id that was not found.</summary>
    private static Stream Open(string logicalName)
    {
        Assembly assembly = typeof(EmbeddedResource).Assembly;

        return assembly.GetManifestResourceStream(logicalName)
               ?? throw new InvalidOperationException($"Embedded resource '{logicalName}' not found.");
    }
}