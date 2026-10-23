using System.Text.Json;
using ModelContextProtocol.Client;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Provides readers over an advertised tool's input schema, as a client sees it over a real
///     <c>tools/list</c>.
/// </summary>
/// <remarks>
///     One spelling each, so every test that reads the surface walks a schema the same way.
/// </remarks>
internal static class ToolSchemas
{
    /// <summary>Returns the schema of the parameter called <paramref name="propertyName" />.</summary>
    /// <remarks>Throws, naming the tool and what it does advertise, when the tool has no such parameter.</remarks>
    public static JsonElement PropertySchema(this McpClientTool tool, string propertyName)
    {
        if (tool.JsonSchema.TryGetProperty("properties", out JsonElement properties)
            && properties.TryGetProperty(propertyName, out JsonElement schema))
            return schema;

        throw new KeyNotFoundException(
            $"{tool.Name} advertises no parameter \"{propertyName}\"; it advertises [{string.Join(", ", tool.PropertyNames())}].");
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
        JsonElement schema = tool.PropertySchema(propertyName);
        if (!schema.TryGetProperty("enum", out JsonElement values))
            throw new KeyNotFoundException($"{tool.Name}.{propertyName} advertises no enum array: {schema}");

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
}