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
    ///     <see cref="McpServerToolTypeAttribute" />-annotated classes, paired with its attribute —
    ///     the one place "which methods are tools" is decided, so consumers cannot drift on the rule.
    /// </summary>
    internal static IEnumerable<(MethodInfo Method, McpServerToolAttribute Attribute)> GetToolMethods()
    {
        return typeof(ToolAttributeDiscovery).Assembly
            .GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(tool => tool.Attribute is not null)
            .Select(tool => (tool.Method, tool.Attribute!));
    }
}