using System.Reflection;

namespace Zphil.ReSharperCli.Infrastructure;

/// <summary>
///     The running server's version, read once from the assembly's
///     <see cref="AssemblyInformationalVersionAttribute" />. <see cref="Informational" /> is the full value
///     (including any <c>+{commit}</c> build metadata) printed by <c>--version</c>; <see cref="SemVer" /> is
///     that value truncated at the first <c>+</c>, for the MCP <c>serverInfo</c> handshake.
/// </summary>
internal static class ServerVersion
{
    /// <summary>The full informational version, e.g. <c>1.0.0+abc123</c>, or <c>"unknown"</c> if unattributed.</summary>
    internal static readonly string Informational =
        typeof(ServerVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    /// <summary>The SemVer core, e.g. <c>1.0.0</c>: <see cref="Informational" /> up to the first <c>+</c>.</summary>
    internal static readonly string SemVer = Informational.Split('+', 2)[0];
}