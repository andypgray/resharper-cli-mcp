using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zphil.ReSharperCli.Pipeline;

/// <summary>
///     Coerces malformed enum-array tool inputs into the array the caller clearly intended.
///     Models routinely send a bare string (<c>"Warning"</c>) or a JSON-encoded array of strings
///     (<c>"[\"Warning\",\"Error\"]"</c>) where an enum array is advertised; the SDK default
///     surfaces both as a generic byte-position deserializer error. Enum analog of
///     <see cref="StringArrayCoercerFactory" />, with element validation via
///     <see cref="EnumValidationConverterFactory" />'s "name + <c>Enum.IsDefined</c>" rule.
/// </summary>
/// <remarks>
///     <para>
///         No tool currently advertises an enum-array parameter; this factory is the symmetric
///         half of "enum coercion" (the array analog of <see cref="EnumValidationConverterFactory" />),
///         kept so a future enum-array parameter is coerced the same forgiving way its scalar and
///         <c>string[]</c> siblings already are. Handled token shapes for any <c>TEnum[]</c>
///         parameter where <c>TEnum</c> is an enum:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <c>StartArray</c> → read each element as a string, parse via
///                 <see cref="Enum.TryParse{T}(string,bool,out T)" /> and <see cref="Enum.IsDefined" />.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <c>String</c> whose contents parse as a JSON array of strings → unwrap and
///                 map each element through the same parse path.
///             </description>
///         </item>
///         <item>
///             <description>
///                 Any other <c>String</c> → single-element <c>[parsed]</c>.
///             </description>
///         </item>
///         <item>
///             <description>
///                 Unknown enum names (at either the top-level string or inside an array) throw
///                 <see cref="UserErrorException" /> with the full valid-values list — same
///                 message shape as <see cref="EnumValidationConverterFactory" />.
///             </description>
///         </item>
///         <item>
///             <description>
///                 Numbers, booleans, objects, or arrays containing non-string elements throw
///                 <see cref="UserErrorException" /> naming the offending token kind. Integer
///                 elements are deliberately NOT admitted as enum values.
///             </description>
///         </item>
///     </list>
/// </remarks>
internal sealed class EnumArrayCoercerFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert.IsArray && typeToConvert.GetElementType()!.IsEnum;
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        Type elementType = typeToConvert.GetElementType()!;
        Type converterType = typeof(EnumArrayCoercer<>).MakeGenericType(elementType);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }

    private sealed class EnumArrayCoercer<T> : JsonConverter<T[]> where T : struct, Enum
    {
        public override T[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartArray:
                    return ReadArray(ref reader);

                case JsonTokenType.String:
                    string value = reader.GetString()!;
                    if (TryParseAsJsonStringArray(value, out var unwrapped)) return unwrapped;

                    return [ParseElement(value)];

                default:
                    throw new UserErrorException(
                        $"Expected a JSON array of {typeof(T).Name}; got {reader.TokenType}.");
            }
        }

        public override void Write(Utf8JsonWriter writer, T[] value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (T element in value) writer.WriteStringValue(element.ToString());

            writer.WriteEndArray();
        }

        /// <summary>
        ///     Reads tokens until the matching <see cref="JsonTokenType.EndArray" />. Hand-rolled
        ///     to avoid recursing through <see cref="EnumValidationConverterFactory" /> per element;
        ///     keeps parity with <see cref="StringArrayCoercerFactory" />'s <c>ReadArray</c>.
        /// </summary>
        private static T[] ReadArray(ref Utf8JsonReader reader)
        {
            List<T> items = new();
            while (reader.Read())
                switch (reader.TokenType)
                {
                    case JsonTokenType.EndArray:
                        return items.ToArray();
                    case JsonTokenType.String:
                        items.Add(ParseElement(reader.GetString()!));
                        break;
                    default:
                        throw new UserErrorException(
                            $"Expected a JSON array of {typeof(T).Name}; got element of type {reader.TokenType}.");
                }

            throw new JsonException("Unexpected end of JSON while reading array.");
        }

        /// <summary>
        ///     Returns <c>true</c> only when <paramref name="value" /> parses as a JSON array
        ///     whose every element is a JSON string. Each string is then mapped to an enum value
        ///     via <see cref="ParseElement" />, which may throw <see cref="UserErrorException" />
        ///     for an unknown name — propagated to the caller so the model gets the valid-values
        ///     list instead of falling through to single-coerce.
        /// </summary>
        private static bool TryParseAsJsonStringArray(string value, out T[] result)
        {
            result = [];

            var trimmed = value.AsSpan().Trim();
            if (trimmed.Length == 0 || trimmed[0] != '[') return false;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(trimmed.ToString());
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;

                List<T> items = new(doc.RootElement.GetArrayLength());
                foreach (JsonElement element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String) return false;

                    items.Add(ParseElement(element.GetString()!));
                }

                result = items.ToArray();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static T ParseElement(string name)
        {
            // A numeric string ("5", "+5", " 5 ") would bind to an ordinal via Enum.TryParse,
            // violating the "integers not admitted" contract — reject before parsing.
            if (EnumStringHelper.LooksNumeric(name)) throw new UserErrorException(BuildMessage(name));

            if (Enum.TryParse(name, true, out T parsed) && Enum.IsDefined(typeof(T), parsed)) return parsed;

            throw new UserErrorException(BuildMessage(name));
        }

        private static string BuildMessage(string? attempted)
        {
            return $"Invalid value \"{attempted}\" for parameter. Valid values: {string.Join(", ", Enum.GetNames<T>())}.";
        }
    }
}