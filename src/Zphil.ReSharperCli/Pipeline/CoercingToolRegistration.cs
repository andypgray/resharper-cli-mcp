using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Zphil.ReSharperCli.Pipeline;

/// <summary>
///     Replaces <c>WithToolsFromAssembly</c> with a per-tool registration that can pass
///     <c>McpServerToolCreateOptions</c>. It exists to do exactly two things
///     <c>WithToolsFromAssembly</c> cannot: wire <see cref="ToolInputSerializerOptions" /> (the
///     forgiving-input coercers) into each tool's argument binder, and re-inject the schema shape
///     those coercers erase.
/// </summary>
/// <remarks>
///     <para>
///         Registering a custom <see cref="System.Text.Json.Serialization.JsonConverter" /> for a
///         type makes STJ's schema exporter emit <c>{}</c> for that parameter — it can no longer
///         infer the shape. Every converter-bound parameter is affected: <c>resharper_cleanup</c>'s
///         <c>files</c> would collapse from an array-of-strings to <c>{}</c>, <c>resharper_inspect</c>'s
///         <c>severity</c> would lose its string type, and (verified empirically) every scalar
///         <c>string?</c> parameter erases too. <see cref="ReinjectErasedSchema" /> is the
///         <c>SchemaCreateOptions.TransformSchemaNode</c> hook that restores the erased shape from the
///         underlying CLR type.
///     </para>
///     <para>
///         This is deliberately narrow: it wires only the coercers and re-injects the schema they
///         erase, and does NOT touch tool metadata (<c>Title</c>/<c>Execution</c>) or shrink the
///         payload — those would change this server's advertised <c>tools/list</c> output and are
///         out of scope.
///     </para>
/// </remarks>
internal static class CoercingToolRegistration
{
    private static readonly AIJsonSchemaCreateOptions SchemaOptions = new()
    {
        TransformSchemaNode = ReinjectErasedSchema
    };

    /// <summary>
    ///     Registers each <see cref="McpServerToolAttribute" />-annotated method as an
    ///     <see cref="McpServerTool" /> whose argument binder uses <see cref="ToolInputSerializerOptions" />
    ///     and whose input schema is repaired by <see cref="ReinjectErasedSchema" />.
    /// </summary>
    public static IMcpServerBuilder WithCoercingTools(this IMcpServerBuilder builder)
    {
        foreach (MethodInfo toolMethod in ToolAttributeDiscovery.GetToolMethods())
        {
            if (toolMethod.GetCustomAttribute<McpServerToolAttribute>() is null) continue;

            Type toolType = toolMethod.DeclaringType
                            ?? throw new InvalidOperationException(
                                $"Tool method '{toolMethod.Name}' has no declaring type.");

            builder.Services.AddSingleton<McpServerTool>(services =>
            {
                McpServerToolCreateOptions options = new()
                {
                    Services = services,
                    SchemaCreateOptions = SchemaOptions,
                    SerializerOptions = ToolInputSerializerOptions.Instance
                };

                // ResharperTools is non-static and its ctor deps (ConfigResolver, InspectService,
                // CleanupService) are all singletons, so the per-request activation is cheap. A null
                // request-service-provider is an unrecoverable host misconfiguration, not a runtime
                // input, so fail loud and clear rather than falling back to a parameterless ctor
                // this type does not have.
                return McpServerTool.Create(
                    toolMethod,
                    r => ActivatorUtilities.CreateInstance(
                        r.Services ?? throw new InvalidOperationException(
                            $"Cannot activate tool '{toolType.Name}': no service provider on the request."),
                        toolType),
                    options);
            });
        }

        return builder;
    }

    /// <summary>
    ///     Re-injects the shape the custom converters erased to <c>{}</c>, reading it back from the
    ///     underlying CLR type: array + <c>items</c> for string/enum arrays, <c>string</c> for scalars,
    ///     and — for enum parameters — the <c>enum</c> value list. The value list is otherwise lost
    ///     entirely (the converter hides the enum from the exporter), so restoring it here is what lets
    ///     the allowed values travel in the schema rather than being duplicated into the description
    ///     prose. Every branch is guarded on <c>!ContainsKey</c>, so it is a no-op whenever the exporter
    ///     already emitted the shape.
    /// </summary>
    private static JsonNode ReinjectErasedSchema(AIJsonSchemaCreateContext context, JsonNode node)
    {
        if (node is JsonObject obj)
        {
            Type t = Nullable.GetUnderlyingType(context.TypeInfo.Type) ?? context.TypeInfo.Type;
            if (t == typeof(string[]) || (t.IsArray && t.GetElementType() is { IsEnum: true }))
            {
                if (!obj.ContainsKey("type")) obj["type"] = "array";

                if (!obj.ContainsKey("items"))
                {
                    JsonObject items = new() { ["type"] = "string" };
                    if (t.GetElementType() is { IsEnum: true } elementType) items["enum"] = EnumNames(elementType);
                    obj["items"] = items;
                }
            }
            else if (t.IsEnum && !obj.ContainsKey("type"))
            {
                obj["type"] = "string";
                obj["enum"] = EnumNames(t);
            }
            else if (t == typeof(string) && !obj.ContainsKey("type"))
            {
                // Load-bearing here: verified empirically that this project's exporter erases every
                // scalar string?/string parameter (solutionPath, profile) to {} under
                // StringCoercerFactory. Without this repair they would advertise no type at all.
                // (The advertised shape is a plain "string"; no ["string","null"] union appears.)
                obj["type"] = "string";
            }
        }

        return node;
    }

    /// <summary>The enum member names as a JSON string array, for a schema <c>enum</c> constraint.</summary>
    private static JsonArray EnumNames(Type enumType)
    {
        JsonArray names = [];
        foreach (string name in Enum.GetNames(enumType)) names.Add(JsonValue.Create(name));

        return names;
    }
}