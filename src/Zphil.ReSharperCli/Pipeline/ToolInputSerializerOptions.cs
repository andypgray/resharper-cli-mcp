using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Zphil.ReSharperCli.Pipeline;

/// <summary>
///     <see cref="JsonSerializerOptions" /> used by <c>AIFunctionFactory</c> when marshalling
///     JSON-RPC tool-call arguments into typed parameters. Its converter factories replace SDK defaults
///     that would otherwise surface user-facing input errors as opaque deserializer messages.
/// </summary>
/// <remarks>
///     An explicit <see cref="DefaultJsonTypeInfoResolver" /> is required on .NET 10 because
///     <c>AIFunctionFactory</c> calls <c>MakeReadOnly()</c> on the options, which throws
///     without a resolver in place.
/// </remarks>
internal static class ToolInputSerializerOptions
{
    public static readonly JsonSerializerOptions Instance = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters =
        {
            new EnumValidationConverterFactory(),
            new StringArrayCoercerFactory(),
            new StringCoercerFactory()
        }
    };
}