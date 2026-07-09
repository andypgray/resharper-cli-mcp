using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zphil.ReSharperCli.Pipeline;

/// <summary>
///     Coerces malformed <c>string[]</c> tool inputs into the array the caller clearly intended.
///     Models routinely send a JSON-encoded string (<c>"[\"A\",\"B\"]"</c>) where an array is
///     advertised, and one bare string (<c>"A"</c>) where a single-element array is expected;
///     the SDK default surfaces both as a generic byte-position deserializer error that gives
///     the model nothing actionable and burns retries.
/// </summary>
/// <remarks>
///     <para>
///         Handled token shapes for any <c>string[]</c> parameter:
///     </para>
///     <list type="bullet">
///         <item>
///             <description><c>StartArray</c> → read as a normal JSON array of strings.</description>
///         </item>
///         <item>
///             <description>
///                 <c>String</c> whose contents parse as a JSON array of strings → return the
///                 unwrapped array. Surrounding whitespace is tolerated.
///             </description>
///         </item>
///         <item>
///             <description>
///                 Any other <c>String</c> (including the empty string and strings that look like
///                 arrays but are not valid JSON arrays of strings) → return a single-element
///                 array containing the string verbatim.
///             </description>
///         </item>
///         <item>
///             <description>
///                 Any other token (number, object, boolean) → throw <see cref="UserErrorException" />
///                 naming the offending token kind.
///             </description>
///         </item>
///     </list>
/// </remarks>
internal sealed class StringArrayCoercerFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert == typeof(string[]);
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        return new StringArrayCoercer();
    }

    private sealed class StringArrayCoercer : JsonConverter<string[]>
    {
        public override string[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartArray:
                    return ReadArray(ref reader);

                case JsonTokenType.String:
                    string value = reader.GetString()!;
                    if (TryParseAsJsonStringArray(value, out string[] unwrapped)) return unwrapped;

                    return [value];

                default:
                    throw new UserErrorException(
                        $"Expected a JSON array of strings (e.g. [\"X\",\"Y\"]); got {reader.TokenType}.");
            }
        }

        public override void Write(Utf8JsonWriter writer, string[] value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (string element in value) writer.WriteStringValue(element);

            writer.WriteEndArray();
        }

        /// <summary>
        ///     Reads tokens until the matching <see cref="JsonTokenType.EndArray" />. Hand-rolled
        ///     to avoid recursing through <see cref="StringArrayCoercerFactory" /> via
        ///     <c>JsonSerializer.Deserialize&lt;string[]&gt;</c>.
        /// </summary>
        private static string[] ReadArray(ref Utf8JsonReader reader)
        {
            List<string> items = new();
            while (reader.Read())
                switch (reader.TokenType)
                {
                    case JsonTokenType.EndArray:
                        return items.ToArray();
                    case JsonTokenType.String:
                        items.Add(reader.GetString()!);
                        break;
                    default:
                        throw new UserErrorException(
                            $"Expected a JSON array of strings; got element of type {reader.TokenType}.");
                }

            throw new JsonException("Unexpected end of JSON while reading array.");
        }

        /// <summary>
        ///     Returns <c>true</c> only when <paramref name="value" /> parses as a JSON array
        ///     whose every element is a JSON string. Anything else (mixed types, nested arrays,
        ///     malformed JSON, scalar values) returns <c>false</c> so the caller falls back to
        ///     single-element coercion.
        /// </summary>
        private static bool TryParseAsJsonStringArray(string value, out string[] result)
        {
            result = [];

            var trimmed = value.AsSpan().Trim();
            if (trimmed.Length == 0 || trimmed[0] != '[') return false;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(trimmed.ToString());
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;

                List<string> items = new(doc.RootElement.GetArrayLength());
                foreach (JsonElement element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String) return false;

                    items.Add(element.GetString()!);
                }

                result = items.ToArray();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}