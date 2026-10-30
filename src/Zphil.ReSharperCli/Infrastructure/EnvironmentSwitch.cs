namespace Zphil.ReSharperCli.Infrastructure;

/// <summary>
///     Reads an on-by-default environment switch, so every switch this server has spells "off" the same way.
/// </summary>
internal static class EnvironmentSwitch
{
    private static readonly string[] OffSpellings = ["off", "false", "0", "no", "disabled"];

    /// <summary>
    ///     Determines whether a switch is on: a value in <see cref="OffSpellings" /> turns it off, and anything
    ///     else, unset and unrecognised included, leaves the shipped default in place.
    /// </summary>
    /// <remarks>
    ///     Unrecognised values fall back to the default as <see cref="SerilogConfiguration.ParseLogLevel" /> does
    ///     with its own variable.
    /// </remarks>
    public static bool IsOn(string? envValue)
    {
        return !OffSpellings.Contains(envValue?.Trim(), StringComparer.OrdinalIgnoreCase);
    }
}