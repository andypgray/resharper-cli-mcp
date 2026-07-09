using System.Text.Json;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Pipeline;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Tests.Pipeline;

/// <summary>
///     Tests for <see cref="EnumValidationConverterFactory" />: every <c>T : struct, Enum</c>
///     routes through a validating converter that throws <see cref="UserErrorException" /> with the
///     full valid-value list on unknown input. Exercised via <see cref="InspectSeverity" />, the one
///     enum parameter the server advertises today.
/// </summary>
public sealed class EnumValidationConverterTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new EnumValidationConverterFactory() }
    };

    [Fact]
    public void Deserialize_ValidName_RoundTrips()
    {
        // Act
        var result = JsonSerializer.Deserialize<InspectSeverity>("\"Warning\"", Options);

        // Assert
        result.ShouldBe(InspectSeverity.Warning);
    }

    [Fact]
    public void Deserialize_UnknownName_ThrowsUserErrorWithValidList()
    {
        // Act
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<InspectSeverity>("\"HIGH\"", Options));

        // Assert — message names the bad value and every valid enum name.
        ex.Message.ShouldContain("\"HIGH\"");
        ex.Message.ShouldContain("Suggestion");
        ex.Message.ShouldContain("Warning");
        ex.Message.ShouldContain("Error");
    }

    [Fact]
    public void Deserialize_CaseInsensitiveName_Succeeds()
    {
        // Act — lowercase name should parse via ignoreCase.
        var result = JsonSerializer.Deserialize<InspectSeverity>("\"warning\"", Options);

        // Assert
        result.ShouldBe(InspectSeverity.Warning);
    }

    [Fact]
    public void Deserialize_NumericString_ThrowsUserErrorWithValidList()
    {
        // Act — a numeric string ("1") would bind to an ordinal via Enum.TryParse, violating the
        // "integers not admitted" contract. Reject it with the valid-values list.
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<InspectSeverity>("\"1\"", Options));

        // Assert — bad value plus valid names so the model self-corrects to a name.
        ex.Message.ShouldContain("\"1\"");
        ex.Message.ShouldContain("Suggestion");
        ex.Message.ShouldContain("Warning");
        ex.Message.ShouldContain("Error");
    }

    [Fact]
    public void Deserialize_NonStringToken_ThrowsUserErrorWithValidList()
    {
        // Act — the advertised schema is "type": "string"; a client sending a raw number is already
        // violating the contract, so it gets the same valid-values message a bad string would.
        var ex = Should.Throw<UserErrorException>(() =>
            JsonSerializer.Deserialize<InspectSeverity>("1", Options));

        // Assert
        ex.Message.ShouldContain("Suggestion");
        ex.Message.ShouldContain("Warning");
        ex.Message.ShouldContain("Error");
    }
}