using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zphil.ReSharperCli.Pipeline;

/// <summary>
///     Deserializes enum-typed tool parameters from JSON strings. On an unrecognised name
///     (e.g. <c>severity: "HIGH"</c>) throws a <see cref="UserErrorException" /> that lists
///     every valid value so the model can self-correct the next call.
/// </summary>
/// <remarks>
///     <para>
///         Registered via <see cref="CoercingToolRegistration" /> on the
///         <c>McpServerToolCreateOptions.SerializerOptions</c>
///         used by <c>AIFunctionFactory</c> when marshalling JSON-RPC arguments. The default
///         <see cref="JsonStringEnumConverter" /> raises a generic <c>JsonException</c> that
///         surfaces without the valid-value list, forcing the model to guess.
///     </para>
///     <para>
///         Covers every <c>T : struct, Enum</c> in tool parameters. Today the only such parameter is
///         <c>resharper_inspect</c>'s <c>severity</c> (<see cref="Tools.InspectSeverity" />); the factory
///         is generic so any future enum parameter is validated the same way.
///     </para>
/// </remarks>
internal sealed class EnumValidationConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert.IsEnum;
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        Type converterType = typeof(ValidatingJsonStringEnumConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }

    private sealed class ValidatingJsonStringEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // Our advertised schema is "type": "string"; a client that sends a number is already
            // violating the contract, so reject non-strings with the same valid-values message
            // a bad string would get. This keeps the error surface uniform.
            if (reader.TokenType != JsonTokenType.String) throw new UserErrorException(BuildMessage(reader.TokenType.ToString()));

            string? name = reader.GetString();

            // A numeric string ("5", "+5", " 5 ") would bind to an ordinal via Enum.TryParse,
            // violating the "integers not admitted" contract — reject before parsing.
            if (name is not null && EnumStringHelper.LooksNumeric(name)) throw new UserErrorException(BuildMessage(name));

            if (name is not null && Enum.TryParse(name, true, out T parsed) && Enum.IsDefined(typeof(T), parsed)) return parsed;

            throw new UserErrorException(BuildMessage(name));
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }

        private static string BuildMessage(string? attempted)
        {
            return $"Invalid value \"{attempted}\" for parameter. Valid values: {string.Join(", ", Enum.GetNames<T>())}.";
        }
    }
}