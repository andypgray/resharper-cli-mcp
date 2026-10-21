using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zphil.ReSharperCli.Pipeline;

/// <summary>
///     Coerces malformed scalar <c>string</c> tool inputs into the value the caller intended. Symmetric
///     counterpart to <see cref="StringArrayCoercerFactory" />.
/// </summary>
/// <remarks>
///     <para>
///         Models routinely wrap a single value in a one-element array (<c>["A"]</c>) where a scalar
///         <c>string</c> is advertised, so a one-string array unwraps and an empty one reads as absent. A string
///         that looks like an array (<c>"[A]"</c>) is never unwrapped: a literal string argument must survive
///         untouched.
///     </para>
///     <para>
///         <see cref="JsonConverter{T}" /> on <c>string?</c> serves both <c>string</c> and
///         <c>string?</c> parameters: <c>string</c> is a reference type and
///         <see cref="System.Text.Json" /> resolves nullability at the binding layer, not the
///         converter layer.
///     </para>
/// </remarks>
internal sealed class StringCoercerFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert == typeof(string);
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        return new StringCoercer();
    }

    private sealed class StringCoercer : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return reader.GetString();

                case JsonTokenType.Null:
                    return null;

                case JsonTokenType.StartArray:
                    return ReadCoercedFromArray(ref reader);

                default:
                    throw new UserErrorException(
                        $"Expected a string; got {reader.TokenType}.");
            }
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value is null)
                writer.WriteNullValue();
            else
                writer.WriteStringValue(value);
        }

        /// <summary>
        ///     Reads a JSON array opened by the caller and collapses it to its one string element, or to
        ///     <see langword="null" /> when it is empty.
        /// </summary>
        /// <remarks>
        ///     Hand-rolled to avoid recursing through <see cref="StringCoercerFactory" /> via
        ///     <c>JsonSerializer.Deserialize&lt;string&gt;</c>.
        /// </remarks>
        private static string? ReadCoercedFromArray(ref Utf8JsonReader reader)
        {
            if (!reader.Read()) throw new JsonException("Unexpected end of JSON while reading array.");

            if (reader.TokenType == JsonTokenType.EndArray) return null;

            if (reader.TokenType != JsonTokenType.String)
                throw new UserErrorException(
                    $"Expected a string; got array element of type {reader.TokenType}.");

            string value = reader.GetString()!;

            if (!reader.Read()) throw new JsonException("Unexpected end of JSON while reading array.");

            if (reader.TokenType != JsonTokenType.EndArray)
                throw new UserErrorException(
                    "Expected a string; got an array with multiple elements. " +
                    "Pass a scalar string, not an array.");

            return value;
        }
    }
}