using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Readers over what a client sees of this server: an advertised tool's input schema and a call's text
///     result.
/// </summary>
/// <remarks>
///     One spelling each, so every test that reads the surface over a real <c>tools/list</c> or
///     <c>tools/call</c> walks a schema the same way.
/// </remarks>
internal static class ToolSchemas
{
    /// <summary>The schema of the parameter called <paramref name="propertyName" />; throws when the tool has none.</summary>
    public static JsonElement PropertySchema(this McpClientTool tool, string propertyName)
    {
        return tool.JsonSchema.GetProperty("properties").GetProperty(propertyName);
    }

    /// <summary>The names of every parameter the tool advertises, or empty when its schema lists none.</summary>
    public static IReadOnlyList<string> PropertyNames(this McpClientTool tool)
    {
        if (!tool.JsonSchema.TryGetProperty("properties", out JsonElement properties)) return [];

        return properties.EnumerateObject().Select(property => property.Name).ToList();
    }

    /// <summary>The values in a parameter's <c>enum</c> array, in declaration order.</summary>
    public static IReadOnlyList<string> EnumValues(this McpClientTool tool, string propertyName)
    {
        JsonElement values = tool.PropertySchema(propertyName).GetProperty("enum");

        return values.EnumerateArray().Select(element => element.GetString()!).ToList();
    }

    /// <summary>The names in the tool's input-schema <c>required</c> array, or empty when it has none.</summary>
    public static IReadOnlyList<string> RequiredProperties(this McpClientTool tool)
    {
        if (!tool.JsonSchema.TryGetProperty("required", out JsonElement required)
            || required.ValueKind != JsonValueKind.Array)
            return [];

        return required.EnumerateArray().Select(element => element.GetString()!).ToList();
    }

    /// <summary>The text of the result's first text block — the whole of what every tool here returns.</summary>
    public static string Text(this CallToolResult result)
    {
        return result.Content.OfType<TextContentBlock>().First().Text;
    }
}