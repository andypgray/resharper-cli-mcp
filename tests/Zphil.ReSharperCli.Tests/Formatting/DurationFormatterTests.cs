using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Formatting;

namespace Zphil.ReSharperCli.Tests.Formatting;

/// <summary>
///     The one spelling of a duration, pinned: the run cap in a timeout message, the elapsed time on a
///     progress line, and the uptime on the shutdown line all read through it.
/// </summary>
public sealed class DurationFormatterTests
{
    [Theory]
    [InlineData(1, "1 second")]
    [InlineData(30, "30 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(300, "5 minutes")]
    [InlineData(600, "10 minutes")]
    public void Format_WholeUnits_RendersThemWithCorrectPluralization(int seconds, string expected)
    {
        // Act
        string formatted = DurationFormatter.Format(TimeSpan.FromSeconds(seconds));

        // Assert
        formatted.ShouldBe(expected);
    }

    [Theory]
    [InlineData(61, "1 minute 1 second")]
    [InlineData(90, "1 minute 30 seconds")]
    [InlineData(455, "7 minutes 35 seconds")]
    public void Format_LeftoverSeconds_SaysThemRatherThanRoundingIntoTheMinutes(int seconds, string expected)
    {
        // The run cap is configured in seconds, so rounding 90 up to "2 minutes" would report a cap the
        // user never set and quietly contradict the value in their own config.
        DurationFormatter.Format(TimeSpan.FromSeconds(seconds)).ShouldBe(expected);
    }
}