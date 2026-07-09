using Serilog.Events;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Tests.Infrastructure;

/// <summary>
///     <see cref="SerilogConfiguration.ParseLogLevel" /> accepts both Microsoft.Extensions.Logging and
///     Serilog level names (case-insensitively) and falls back to <see cref="LogEventLevel.Warning" /> for
///     anything it cannot recognise, including numeric strings that would otherwise bind to an undefined
///     enum value.
/// </summary>
public sealed class SerilogConfigurationTests
{
    [Theory]
    // Microsoft.Extensions.Logging names map through LevelConvert.
    [InlineData("Information", LogEventLevel.Information)]
    [InlineData("Trace", LogEventLevel.Verbose)]
    [InlineData("Critical", LogEventLevel.Fatal)]
    // Serilog names are accepted directly.
    [InlineData("Verbose", LogEventLevel.Verbose)]
    [InlineData("Fatal", LogEventLevel.Fatal)]
    // Case-insensitive.
    [InlineData("eRRoR", LogEventLevel.Error)]
    // Unrecognised input falls back to Warning.
    [InlineData("99", LogEventLevel.Warning)]
    [InlineData("nonsense", LogEventLevel.Warning)]
    [InlineData("", LogEventLevel.Warning)]
    [InlineData("   ", LogEventLevel.Warning)]
    [InlineData(null, LogEventLevel.Warning)]
    public void ParseLogLevel_MapsKnownNamesAndFallsBackToWarning(string? envValue, LogEventLevel expected)
    {
        // Act
        LogEventLevel level = SerilogConfiguration.ParseLogLevel(envValue);

        // Assert
        level.ShouldBe(expected);
    }
}