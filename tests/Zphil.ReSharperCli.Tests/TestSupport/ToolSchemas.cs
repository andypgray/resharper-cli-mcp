using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Provides readers over what a client sees of this server: an advertised tool's input schema and a call's
///     text result.
/// </summary>
/// <remarks>
///     One spelling each, so every test that reads the surface over a real <c>tools/list</c> or
///     <c>tools/call</c> walks a schema the same way.
/// </remarks>
internal static class ToolSchemas
{
    /// <summary>Returns the schema of the parameter called <paramref name="propertyName" />.</summary>
    /// <remarks>Throws when the tool has no such parameter.</remarks>
    public static JsonElement PropertySchema(this McpClientTool tool, string propertyName)
    {
        return tool.JsonSchema.GetProperty("properties").GetProperty(propertyName);
    }

    /// <summary>Returns every parameter name the tool advertises, or empty when its schema lists none.</summary>
    public static IReadOnlyList<string> PropertyNames(this McpClientTool tool)
    {
        if (!tool.JsonSchema.TryGetProperty("properties", out JsonElement properties)) return [];

        return properties.EnumerateObject().Select(property => property.Name).ToList();
    }

    /// <summary>Returns the values in a parameter's <c>enum</c> array, in declaration order.</summary>
    public static IReadOnlyList<string> EnumValues(this McpClientTool tool, string propertyName)
    {
        JsonElement values = tool.PropertySchema(propertyName).GetProperty("enum");

        return values.EnumerateArray().Select(element => element.GetString()!).ToList();
    }

    /// <summary>Returns the tool's input-schema <c>required</c> names, or empty when it has none.</summary>
    public static IReadOnlyList<string> RequiredProperties(this McpClientTool tool)
    {
        if (!tool.JsonSchema.TryGetProperty("required", out JsonElement required)
            || required.ValueKind != JsonValueKind.Array)
            return [];

        return required.EnumerateArray().Select(element => element.GetString()!).ToList();
    }

    /// <summary>Returns the text of the first text block, where every tool here puts its whole answer.</summary>
    public static string Text(this CallToolResult result)
    {
        return result.Content.OfType<TextContentBlock>().First().Text;
    }
}