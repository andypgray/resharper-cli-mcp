using System.Text.Json;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Pipeline;

namespace Zphil.ReSharperCli.Tests.Pipeline;

/// <summary>
///     Tests for <see cref="StringCoercerFactory" />: every <c>string</c>/<c>string?</c> tool
///     parameter accepts a plain string, a single-element array (unwrapped), or an empty array
///     (treated as <c>null</c>), and rejects everything else with a friendly
///     <see cref="UserErrorException" />.
/// </summary>
public sealed class StringCoercerFactoryTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new StringCoercerFactory() }
    };

    [Theory]
    // The canonical, well-formed input shape.
    [InlineData("A")]
    // An empty string passes through verbatim; it is NOT coerced to null.
    [InlineData("")]
    // A literal string that looks like a JSON array must NOT be unwrapped.
    [InlineData("[A]")]
    public void Deserialize_JsonString_PassesThroughVerbatim(string value)
    {
        // Arrange
        string json = JsonSerializer.Serialize(value);

        // Act
        var result = JsonSerializer.Deserialize<string>(json, Options);

        // Assert
        result.ShouldBe(value);
    }

    [Fact]
    public void Deserialize_SingleElementArray_UnwrapsToString()
    {
        // Act — the headline case: model wrapped a scalar in a one-element array.
        var result = JsonSerializer.Deserialize<string>("""["A"]""", Options);

        // Assert
        result.ShouldBe("A");
    }

    [Theory]
    [InlineData("null")]
    // An empty array is treated as "absent", so a nullable parameter gets a clean unset.
    [InlineData("[]")]
    public void Deserialize_AbsentValue_ReturnsNull(string json)
    {
        // Act
        var result = JsonSerializer.Deserialize<string>(json, Options);

        // Assert
        result.ShouldBeNull();
    }

    [Theory]
    // Ambiguous: the model meant string[], not string.
    [InlineData("""["A","B"]""", "Expected a string; got an array with multiple elements. Pass a scalar string, not an array.")]
    [InlineData("[42]", "Expected a string; got array element of type Number.")]
    // A null element is not silently admitted; downstream consumers expect non-null.
    [InlineData("[null]", "Expected a string; got array element of type Null.")]
    [InlineData("42", "Expected a string; got Number.")]
    [InlineData("{}", "Expected a string; got StartObject.")]
    [InlineData("true", "Expected a string; got True.")]
    public void Deserialize_AnythingButAStringOrOneStringArray_ThrowsUserErrorNamingWhatItGot(
        string json,
        string message)
    {
        // Act
        var ex = Should.Throw<UserErrorException>(() => JsonSerializer.Deserialize<string>(json, Options));

        // Assert — the message names the offending token kind so the model can self-correct.
        ex.Message.ShouldBe(message);
    }
}