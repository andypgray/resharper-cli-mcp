using System.Reflection;
using ModelContextProtocol.Server;

namespace Zphil.ReSharperCli.Pipeline;

/// <summary>
///     Reflects over the current assembly to discover MCP tool methods.
/// </summary>
internal static class ToolAttributeDiscovery
{
    /// <summary>
    ///     Returns every <see cref="McpServerToolAttribute" />-annotated method on
    ///     <see cref="McpServerToolTypeAttribute" />-annotated classes — the one place "which methods are
    ///     tools" is decided, so consumers cannot drift on the rule.
    /// </summary>
    internal static IEnumerable<MethodInfo> GetToolMethods()
    {
        return typeof(ToolAttributeDiscovery).Assembly
            .GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null);
    }
}