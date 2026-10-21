namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     Formats a duration as human-readable, correctly pluralized text, such as "30 seconds", "5 minutes" or
///     "1 minute 30 seconds".
/// </summary>
/// <remarks>
///     The one spelling for every surface that names a span of time, so the same value cannot read differently
///     in two places. The leftover seconds are spelled out rather than rounded into the minute count because the
///     run cap is configured in seconds: a cap someone set to 90 must not report itself as two minutes, or the
///     message contradicts the value they chose.
/// </remarks>
internal static class DurationFormatter
{
    public static string Format(TimeSpan duration)
    {
        int totalSeconds = Math.Max(1, (int)Math.Round(duration.TotalSeconds, MidpointRounding.AwayFromZero));
        if (totalSeconds < 60) return Pluralize(totalSeconds, "second");

        int minutes = totalSeconds / 60;
        int seconds = totalSeconds % 60;

        return seconds == 0
            ? Pluralize(minutes, "minute")
            : $"{Pluralize(minutes, "minute")} {Pluralize(seconds, "second")}";
    }

    private static string Pluralize(int count, string unit)
    {
        return count == 1 ? $"1 {unit}" : $"{count} {unit}s";
    }
}