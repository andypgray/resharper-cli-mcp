using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Zphil.ReSharperCli.Pipeline;

/// <summary>
///     <see cref="JsonSerializerOptions" /> used by <c>AIFunctionFactory</c> when marshalling
///     JSON-RPC tool-call arguments into typed parameters. Custom converter factories replace
///     SDK defaults that would otherwise surface user-facing input errors as opaque
///     deserializer messages:
///     <list type="bullet">
///         <item>
///             <description>
///                 <see cref="EnumValidationConverterFactory" /> — invalid enum names throw
///                 <see cref="UserErrorException" /> listing every valid value, so the client
///                 can self-correct on the next call instead of seeing a generic JSON parse error.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="EnumArrayCoercerFactory" /> — enum-array analog of
///                 <see cref="StringArrayCoercerFactory" />: silently coerces a stringified
///                 array (<c>"[\"Warning\",\"Error\"]"</c>) and a bare string (<c>"Warning"</c>)
///                 into the enum array the model intended, validating each element via
///                 <see cref="Enum.IsDefined" /> with the same valid-values message as
///                 <see cref="EnumValidationConverterFactory" />.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="StringArrayCoercerFactory" /> — silently coerces stringified arrays
///                 (<c>"[\"A\",\"B\"]"</c>) and bare strings (<c>"A"</c>) into the
///                 <c>string[]</c> the model intended, instead of failing with a byte-position
///                 deserializer error that gives the model nothing actionable.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="StringCoercerFactory" /> — the symmetric counterpart: silently
///                 unwraps a single-element array (<c>["A"]</c>) into the scalar <c>string</c>
///                 the model intended, and coerces an empty array (<c>[]</c>) to <c>null</c>.
///                 Multi-element arrays and non-string tokens still throw
///                 <see cref="UserErrorException" /> naming the offending token kind.
///             </description>
///         </item>
///     </list>
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
            new EnumArrayCoercerFactory(),
            new StringArrayCoercerFactory(),
            new StringCoercerFactory()
        }
    };
}