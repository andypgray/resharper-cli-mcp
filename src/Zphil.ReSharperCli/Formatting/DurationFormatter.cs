namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     Human-readable, correctly-pluralized durations: "30 seconds", "5 minutes", "1 minute 30 seconds".
///     The one spelling for every surface that names a span of time — the run cap in a timeout message, the
///     elapsed time on a progress line, the lock's contention error, the uptime on the shutdown line — so
///     the same value cannot read differently in two places.
/// </summary>
/// <remarks>
///     The leftover seconds are spelled out rather than rounded into the minute count because the run cap
///     is configured <em>in</em> seconds — a cap someone set to 90 must not report itself as two minutes,
///     or the message contradicts the value they chose.
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