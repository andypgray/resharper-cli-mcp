using System.Reflection;
using ModelContextProtocol.Server;

namespace Zphil.ReSharperCli.Pipeline;

/// <summary>
///     Reflects over the current assembly to discover MCP tool methods.
/// </summary>
internal static class ToolAttributeDiscovery
{
    /// <summary>
    ///     Returns all public methods on <see cref="McpServerToolTypeAttribute" />-annotated classes.
    /// </summary>
    internal static IEnumerable<MethodInfo> GetToolMethods()
    {
        return typeof(ToolAttributeDiscovery).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
    }
}