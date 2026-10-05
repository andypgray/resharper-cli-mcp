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

    /// <summary>The text inside a JSON string that holds a JSON string array, and the array it unwraps to.</summary>
    public static TheoryData<string, string[]> StringifiedArrays =>
        new()
        {
            // The dominant malformed shape the model produces.
            { """["A","B"]""", ["A", "B"] },
            // Whitespace around the inner JSON is tolerated.
            { """  ["A"]  """, ["A"] },
            // The most common single-item form.
            { """["IShape"]""", ["IShape"] }
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

    [Theory]
    [MemberData(nameof(StringifiedArrays))]
    public void Deserialize_StringHoldingAJsonStringArray_UnwrapsToThatArray(string inner, string[] expected)
    {
        // Arrange — outer JSON is a string whose contents are themselves a JSON array.
        string json = JsonSerializer.Serialize(inner);

        // Act
        string[] result = JsonSerializer.Deserialize<string[]>(json, Options)!;

        // Assert
        result.ShouldBe(expected);
    }

    [Theory]
    // A bare scalar where an array is expected.
    [InlineData("IShape")]
    // An empty string is not a JSON array, so it is single-coerced too.
    [InlineData("")]
    // Starts with `[` but is not valid JSON.
    [InlineData("[broken")]
    // Valid JSON array, but of numbers: kept verbatim rather than silently stringifying the numbers.
    [InlineData("[1,2]")]
    // The stringified twin of the literal ["A",null] refused below: it never reaches the element check, so it
    // is one bare string like any other non-string-array text — an intentional asymmetry of the forgiving-input
    // policy, not a mirror of that path.
    [InlineData("""["A",null]""")]
    public void Deserialize_StringThatIsNotAJsonStringArray_CoercesToThatOneElement(string value)
    {
        // Arrange
        string json = JsonSerializer.Serialize(value);

        // Act
        string[] result = JsonSerializer.Deserialize<string[]>(json, Options)!;

        // Assert
        result.ShouldBe([value]);
    }

    [Theory]
    [InlineData("42", "Number")]
    [InlineData("{}", "StartObject")]
    [InlineData("true", "True")]
    public void Deserialize_TopLevelTokenThatIsNoStringOrArray_ThrowsUserErrorNamingItsKind(string json, string kind)
    {
        // Act
        var ex = Should.Throw<UserErrorException>(() => JsonSerializer.Deserialize<string[]>(json, Options));

        // Assert — the message names the offending token kind so the model can self-correct.
        ex.Message.ShouldBe($"Expected a JSON array of strings (e.g. [\"X\",\"Y\"]); got {kind}.");
    }

    [Theory]
    // A clean error, not a generic byte-position deserializer message.
    [InlineData("""["A",1]""", "Number")]
    // Silently admitting null into string[] would NRE the downstream services that consume the array.
    [InlineData("""["A",null]""", "Null")]
    public void Deserialize_ArrayWithANonStringElement_ThrowsUserErrorNamingItsKind(string json, string kind)
    {
        // Act
        var ex = Should.Throw<UserErrorException>(() => JsonSerializer.Deserialize<string[]>(json, Options));

        // Assert
        ex.Message.ShouldBe($"Expected a JSON array of strings; got element of type {kind}.");
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