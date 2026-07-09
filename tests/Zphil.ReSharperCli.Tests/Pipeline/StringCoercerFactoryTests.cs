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

    [Fact]
    public void Deserialize_PlainString_ReturnsString()
    {
        // Act — the canonical, well-formed input shape.
        var result = JsonSerializer.Deserialize<string>("\"A\"", Options);

        // Assert
        result.ShouldBe("A");
    }

    [Fact]
    public void Deserialize_Null_ReturnsNull()
    {
        // Act
        var result = JsonSerializer.Deserialize<string>("null", Options);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public void Deserialize_EmptyString_ReturnsEmptyString()
    {
        // Act — empty string passes through verbatim; we do NOT coerce it to null.
        var result = JsonSerializer.Deserialize<string>("\"\"", Options);

        // Assert
        result.ShouldBe("");
    }

    [Fact]
    public void Deserialize_StringContainingBrackets_ReturnsLiteralString()
    {
        // Arrange — literal string that looks like a JSON array. Must NOT be unwrapped:
        // a scalar string argument is passed through verbatim.
        string json = JsonSerializer.Serialize("[A]");

        // Act
        var result = JsonSerializer.Deserialize<string>(json, Options);

        // Assert
        result.ShouldBe("[A]");
    }

    [Fact]
    public void Deserialize_SingleElementArray_UnwrapsToString()
    {
        // Act — the headline case: model wrapped a scalar in a one-element array.
        var result = JsonSerializer.Deserialize<string>("""["A"]""", Options);

        // Assert
        result.ShouldBe("A");
    }

    [Fact]
    public void Deserialize_EmptyArray_ReturnsNull()
    {
        // Act — empty array is treated as "absent"; nullable params get a clean unset.
        var result = JsonSerializer.Deserialize<string>("[]", Options);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public void Deserialize_MultiElementArray_ThrowsUserError()
    {
        // Act — multi-element arrays are ambiguous: the model meant string[], not string.
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<string>("""["A","B"]""", Options));

        // Assert
        ex.Message.ShouldContain("multiple elements");
    }

    [Fact]
    public void Deserialize_ArrayContainingNumber_ThrowsUserError()
    {
        // Act
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<string>("[42]", Options));

        // Assert — message names the offending element kind so the model can self-correct.
        ex.Message.ShouldContain("Number");
    }

    [Fact]
    public void Deserialize_ArrayContainingNull_ThrowsUserError()
    {
        // Act — a null element is not silently admitted; downstream consumers expect non-null.
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<string>("[null]", Options));

        // Assert
        ex.Message.ShouldContain("Null");
    }

    [Fact]
    public void Deserialize_Number_ThrowsUserError()
    {
        // Act
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<string>("42", Options));

        // Assert
        ex.Message.ShouldContain("Number");
    }

    [Fact]
    public void Deserialize_Object_ThrowsUserError()
    {
        // Act
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<string>("{}", Options));

        // Assert
        ex.Message.ShouldContain("StartObject");
    }

    [Fact]
    public void Deserialize_Boolean_ThrowsUserError()
    {
        // Act
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<string>("true", Options));

        // Assert
        ex.Message.ShouldContain("True");
    }
}