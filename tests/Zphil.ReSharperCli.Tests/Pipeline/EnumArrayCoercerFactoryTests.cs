using System.Text.Json;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Pipeline;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Tests.Pipeline;

/// <summary>
///     Tests for <see cref="EnumArrayCoercerFactory" />: an enum-array parameter accepts a plain
///     JSON array, a stringified JSON array, or a bare string (single-coerce), validates each
///     element against <see cref="Enum.IsDefined" />, and rejects everything else with a friendly
///     <see cref="UserErrorException" />. No tool advertises one today; these lock the symmetric
///     enum-array behaviour via <see cref="InspectSeverity" />.
/// </summary>
public sealed class EnumArrayCoercerFactoryTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new EnumArrayCoercerFactory() }
    };

    [Fact]
    public void Deserialize_PlainArray_ReturnsValues()
    {
        // Act — the canonical, well-formed input shape.
        var result = JsonSerializer.Deserialize<InspectSeverity[]>("""["Warning","Error"]""", Options)!;

        // Assert
        result.ShouldBe([InspectSeverity.Warning, InspectSeverity.Error]);
    }

    [Fact]
    public void Deserialize_ScalarString_ReturnsSingleElementArray()
    {
        // Act — bare scalar where the model meant a single-element array.
        var result = JsonSerializer.Deserialize<InspectSeverity[]>("\"Warning\"", Options)!;

        // Assert
        result.ShouldBe([InspectSeverity.Warning]);
    }

    [Fact]
    public void Deserialize_StringifiedJsonArray_Unwraps()
    {
        // Arrange — outer JSON is a string whose contents are themselves a JSON array.
        // The dominant malformed shape the model produces.
        string json = JsonSerializer.Serialize("""["Warning","Error"]""");

        // Act
        var result = JsonSerializer.Deserialize<InspectSeverity[]>(json, Options)!;

        // Assert
        result.ShouldBe([InspectSeverity.Warning, InspectSeverity.Error]);
    }

    [Fact]
    public void Deserialize_StringifiedJsonArrayWithWhitespace_Unwraps()
    {
        // Arrange — tolerate whitespace around the inner JSON.
        string json = JsonSerializer.Serialize("""  ["Warning"]  """);

        // Act
        var result = JsonSerializer.Deserialize<InspectSeverity[]>(json, Options)!;

        // Assert
        result.ShouldBe([InspectSeverity.Warning]);
    }

    [Fact]
    public void Deserialize_CaseInsensitive_Matches()
    {
        // Act — lowercase scalar should parse via ignoreCase.
        var result = JsonSerializer.Deserialize<InspectSeverity[]>("\"warning\"", Options)!;

        // Assert
        result.ShouldBe([InspectSeverity.Warning]);
    }

    [Fact]
    public void Deserialize_CaseInsensitiveInArray_Matches()
    {
        // Act — mixed casing inside a plain array.
        var result = JsonSerializer.Deserialize<InspectSeverity[]>("""["warning","ERROR"]""", Options)!;

        // Assert
        result.ShouldBe([InspectSeverity.Warning, InspectSeverity.Error]);
    }

    [Fact]
    public void Deserialize_EmptyArray_ReturnsEmpty()
    {
        // Act — empty array passes through verbatim. Matches StringArrayCoercerFactory's semantics.
        var result = JsonSerializer.Deserialize<InspectSeverity[]>("[]", Options)!;

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public void Deserialize_Null_ReturnsNull_ForNullableArray()
    {
        // Act — STJ short-circuits null for reference types before invoking the converter.
        var result = JsonSerializer.Deserialize<InspectSeverity[]?>("null", Options);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public void Deserialize_UnknownName_ThrowsWithValidList()
    {
        // Act — unknown enum name surfaces the same valid-values message as
        // EnumValidationConverterFactory; the model can self-correct on the next call.
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<InspectSeverity[]>("""["Frobnicate"]""", Options));

        // Assert — message contains the bad value and every valid enum name.
        ex.Message.ShouldContain("\"Frobnicate\"");
        foreach (string name in Enum.GetNames<InspectSeverity>()) ex.Message.ShouldContain(name);
    }

    [Fact]
    public void Deserialize_UnknownNameInStringifiedArray_ThrowsWithValidList()
    {
        // Arrange — stringified-array path also propagates friendly enum errors,
        // not the JsonException fallback to single-coerce.
        string json = JsonSerializer.Serialize("""["Frobnicate"]""");

        // Act
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<InspectSeverity[]>(json, Options));

        // Assert
        ex.Message.ShouldContain("\"Frobnicate\"");
        ex.Message.ShouldContain("Warning");
    }

    [Fact]
    public void Deserialize_UnknownScalar_ThrowsWithValidList()
    {
        // Act — single-coerce path also validates the enum name.
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<InspectSeverity[]>("\"Frobnicate\"", Options));

        // Assert
        ex.Message.ShouldContain("\"Frobnicate\"");
        ex.Message.ShouldContain("Warning");
    }

    [Fact]
    public void Deserialize_NumericElement_ThrowsUserError()
    {
        // Act — explicit policy: integers are NOT admitted as enum values, even though
        // Enum.TryParse on a number-string would succeed. Plain array path.
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<InspectSeverity[]>("[42]", Options));

        // Assert — message names the offending token kind so the model can self-correct.
        ex.Message.ShouldContain("Number");
    }

    [Fact]
    public void Deserialize_NumericElement_FromStringifiedArray_ThrowsUserError()
    {
        // Arrange — stringified array containing a number is not a valid string array;
        // TryParseAsJsonStringArray rejects it, then single-coerce treats the whole string
        // as one enum name, which fails with the valid-values message.
        string json = JsonSerializer.Serialize("[42]");

        // Act
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<InspectSeverity[]>(json, Options));

        // Assert — verbatim string "[42]" surfaces as the bad enum name.
        ex.Message.ShouldContain("[42]");
    }

    [Fact]
    public void Deserialize_Number_ThrowsUserError()
    {
        // Act — top-level number is the wrong token kind entirely.
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<InspectSeverity[]>("42", Options));

        // Assert
        ex.Message.ShouldContain("Number");
    }

    [Fact]
    public void Deserialize_NumericStringElement_InArray_ThrowsUserError()
    {
        // Act — a numeric STRING ("5") would bind to an ordinal via Enum.TryParse, violating the
        // "integers not admitted" contract. Reject it with the valid-values message.
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<InspectSeverity[]>("""["5"]""", Options));

        // Assert — bad value plus a valid name so the model uses the name, not the integer.
        ex.Message.ShouldContain("\"5\"");
        ex.Message.ShouldContain("Warning");
    }

    [Fact]
    public void Deserialize_NumericStringScalar_ThrowsUserError()
    {
        // Act — the single-coerce (bare scalar) path also rejects a numeric string.
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<InspectSeverity[]>("\"5\"", Options));

        // Assert
        ex.Message.ShouldContain("\"5\"");
        ex.Message.ShouldContain("Warning");
    }

    [Fact]
    public void Deserialize_NumericStringElement_FromStringifiedArray_ThrowsUserError()
    {
        // Arrange — the stringified-array path maps each element through the same guard.
        string json = JsonSerializer.Serialize("""["5"]""");

        // Act
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<InspectSeverity[]>(json, Options));

        // Assert
        ex.Message.ShouldContain("\"5\"");
        ex.Message.ShouldContain("Warning");
    }

    [Fact]
    public void Deserialize_Object_ThrowsUserError()
    {
        // Act
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<InspectSeverity[]>("{}", Options));

        // Assert
        ex.Message.ShouldContain("StartObject");
    }

    [Fact]
    public void Deserialize_Boolean_ThrowsUserError()
    {
        // Act
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<InspectSeverity[]>("true", Options));

        // Assert
        ex.Message.ShouldContain("True");
    }

    [Fact]
    public void Deserialize_NullElement_ThrowsUserError()
    {
        // Act — null elements are rejected so downstream consumers don't NRE on them.
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<InspectSeverity[]>("""["Warning",null]""", Options));

        // Assert
        ex.Message.ShouldContain("Null");
    }

    [Fact]
    public void Deserialize_UnclosedArray_ThrowsJsonException()
    {
        // Act — STJ's outer parser rejects truncated JSON before it ever reaches the converter,
        // but the EndOfStream guard inside ReadArray exists to keep the converter safe if a
        // future caller hands it a primed Utf8JsonReader. Symmetric with StringArrayCoercerFactory.
        Should.Throw<JsonException>(() =>
            JsonSerializer.Deserialize<InspectSeverity[]>("""["Warning","Error" """, Options));
    }
}