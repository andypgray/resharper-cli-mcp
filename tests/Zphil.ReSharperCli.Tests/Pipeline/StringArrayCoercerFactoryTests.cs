using System.Text.Json;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Pipeline;

namespace Zphil.ReSharperCli.Tests.Pipeline;

/// <summary>
///     Tests for <see cref="StringArrayCoercerFactory" />: every <c>string[]</c> tool parameter
///     accepts a plain JSON array, a stringified JSON array, or a bare string (single-coerce),
///     and rejects everything else with a friendly <see cref="UserErrorException" />.
/// </summary>
public sealed class StringArrayCoercerFactoryTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new StringArrayCoercerFactory() }
    };

    [Fact]
    public void Deserialize_PlainArray_ReturnsArray()
    {
        // Act — the canonical, well-formed input shape.
        string[] result = JsonSerializer.Deserialize<string[]>("""["A","B"]""", Options)!;

        // Assert
        result.ShouldBe(["A", "B"]);
    }

    [Fact]
    public void Deserialize_EmptyArray_ReturnsEmpty()
    {
        // Act
        string[] result = JsonSerializer.Deserialize<string[]>("[]", Options)!;

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public void Deserialize_StringifiedArray_UnwrapsToArray()
    {
        // Arrange — outer JSON is a string whose contents are themselves a JSON array.
        // This is the dominant malformed shape the model produces.
        string json = JsonSerializer.Serialize("""["A","B"]""");

        // Act
        string[] result = JsonSerializer.Deserialize<string[]>(json, Options)!;

        // Assert
        result.ShouldBe(["A", "B"]);
    }

    [Fact]
    public void Deserialize_StringifiedArrayWithSurroundingWhitespace_UnwrapsToArray()
    {
        // Arrange — tolerate whitespace around the inner JSON.
        string json = JsonSerializer.Serialize("""  ["A"]  """);

        // Act
        string[] result = JsonSerializer.Deserialize<string[]>(json, Options)!;

        // Assert
        result.ShouldBe(["A"]);
    }

    [Fact]
    public void Deserialize_StringifiedSingletonArray_UnwrapsToArray()
    {
        // Arrange — the most common single-item form.
        string json = JsonSerializer.Serialize("""["IShape"]""");

        // Act
        string[] result = JsonSerializer.Deserialize<string[]>(json, Options)!;

        // Assert
        result.ShouldBe(["IShape"]);
    }

    [Fact]
    public void Deserialize_BareString_CoercesToSingleElementArray()
    {
        // Act — a bare scalar where an array is expected.
        string[] result = JsonSerializer.Deserialize<string[]>("\"IShape\"", Options)!;

        // Assert
        result.ShouldBe(["IShape"]);
    }

    [Fact]
    public void Deserialize_EmptyString_CoercesToSingleEmptyElement()
    {
        // Act — empty string isn't a JSON array, so single-coerce.
        string[] result = JsonSerializer.Deserialize<string[]>("\"\"", Options)!;

        // Assert
        result.ShouldBe([""]);
    }

    [Fact]
    public void Deserialize_StringThatLooksLikeArrayButIsMalformed_CoercesToSingleElement()
    {
        // Arrange — starts with `[` but isn't valid JSON. Falls through to single-coerce.
        string json = JsonSerializer.Serialize("[broken");

        // Act
        string[] result = JsonSerializer.Deserialize<string[]>(json, Options)!;

        // Assert
        result.ShouldBe(["[broken"]);
    }

    [Fact]
    public void Deserialize_StringContainingNonStringArray_CoercesToSingleElement()
    {
        // Arrange — valid JSON array but elements are numbers, not strings.
        string json = JsonSerializer.Serialize("[1,2]");

        // Act
        string[] result = JsonSerializer.Deserialize<string[]>(json, Options)!;

        // Assert — verbatim single-element array; we do NOT silently stringify numbers.
        result.ShouldBe(["[1,2]"]);
    }

    [Fact]
    public void Deserialize_Number_ThrowsUserError()
    {
        // Act
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<string[]>("42", Options));

        // Assert — message names the offending token kind so the model can self-correct.
        ex.Message.ShouldContain("Number");
    }

    [Fact]
    public void Deserialize_Object_ThrowsUserError()
    {
        // Act
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<string[]>("{}", Options));

        // Assert
        ex.Message.ShouldContain("StartObject");
    }

    [Fact]
    public void Deserialize_Boolean_ThrowsUserError()
    {
        // Act
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<string[]>("true", Options));

        // Assert
        ex.Message.ShouldContain("True");
    }

    [Fact]
    public void Deserialize_ArrayContainingNumber_ThrowsUserError()
    {
        // Act — an array element that isn't a string surfaces as a clean error,
        // not a generic byte-position deserializer message.
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<string[]>("""["A",1]""", Options));

        // Assert
        ex.Message.ShouldContain("Number");
    }

    [Fact]
    public void Deserialize_ArrayContainingNull_ThrowsUserError()
    {
        // Act — a null element in a literal array is rejected. (A STRINGIFIED "[\"A\",null]" does
        // NOT reach here: it is treated as a bare string and single-coerced into a one-element array
        // — an intentional asymmetry of the forgiving-input policy, not a mirror of this path.)
        // Silently admitting null into string[] would NRE downstream services that consume the array.
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<string[]>("""["A",null]""", Options));

        // Assert
        ex.Message.ShouldContain("Null");
    }

    [Fact]
    public void Deserialize_UnclosedArray_ThrowsJsonException()
    {
        // Act — STJ's outer parser rejects truncated JSON before it ever reaches the converter,
        // but the EndOfStream guard inside ReadArray exists to keep the converter safe if a
        // future caller hands it a primed Utf8JsonReader.
        Should.Throw<JsonException>(() =>
            JsonSerializer.Deserialize<string[]>("""["A","B" """, Options));
    }
}