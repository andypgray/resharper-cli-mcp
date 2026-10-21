using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Zphil.ReSharperCli.Pipeline;

/// <summary>
///     Provides the <see cref="JsonSerializerOptions" /> <c>AIFunctionFactory</c> uses when marshalling
///     JSON-RPC tool-call arguments into typed parameters.
/// </summary>
/// <remarks>
///     <para>
///         Its converter factories replace SDK defaults that surface a malformed argument as a generic
///         byte-position deserializer error, which gives a model nothing to correct and costs it retries.
///     </para>
///     <para>
///         An explicit <see cref="DefaultJsonTypeInfoResolver" /> is required on .NET 10 because
///         <c>AIFunctionFactory</c> calls <c>MakeReadOnly()</c> on the options, which throws without a
///         resolver in place.
///     </para>
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