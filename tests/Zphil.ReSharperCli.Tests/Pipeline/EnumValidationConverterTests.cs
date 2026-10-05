using System.Text.Json;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Pipeline;
using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Tests.Pipeline;

/// <summary>
///     Pins <see cref="EnumValidationConverterFactory" /> through <see cref="InspectSeverity" />, which stands
///     for every enum parameter the server advertises.
/// </summary>
public sealed class EnumValidationConverterTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new EnumValidationConverterFactory() }
    };

    [Theory]
    [InlineData("Warning")]
    // A lowercase name parses too, via ignoreCase.
    [InlineData("warning")]
    public void Deserialize_AMemberNameInAnyCase_Binds(string name)
    {
        // Arrange
        string json = JsonSerializer.Serialize(name);

        // Act
        var result = JsonSerializer.Deserialize<InspectSeverity>(json, Options);

        // Assert
        result.ShouldBe(InspectSeverity.Warning);
    }

    [Theory]
    [InlineData("HIGH")]
    // A numeric string would bind to an ordinal via Enum.TryParse, violating the "integers not admitted"
    // contract, so it is refused like any unknown name.
    [InlineData("1")]
    public void Deserialize_AStringThatIsNoMemberName_ThrowsNamingItAndTheValidList(string name)
    {
        // Arrange
        string json = JsonSerializer.Serialize(name);

        // Act
        var ex = Should.Throw<UserErrorException>(() => JsonSerializer.Deserialize<InspectSeverity>(json, Options));

        // Assert — the bad value plus every valid name, so the model self-corrects to a name.
        ex.Message.ShouldBe($"Invalid value \"{name}\" for parameter. Valid values: Suggestion, Warning, Error.");
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