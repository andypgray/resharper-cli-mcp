using System.Globalization;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Defines how long one <c>jb</c> run may take, and the environment variable that moves it.
/// </summary>
/// <remarks>
///     <para>
///         The cap exists so a hung <c>jb</c> cannot occupy an agent indefinitely, not as a statement about how
///         long analysis ought to take. MCP clients allow a tool call far longer (Claude Code's limit runs to
///         hours), so every timeout a user meets is one this server chose.
///     </para>
///     <para>
///         The default is sized for the run that approaches it, a cold whole-solution analysis.
///         <c>--include</c> does not make a cold run cheaper, because <c>jb</c> analyses the whole solution and
///         narrows only what it inspects, so there is no scoping lever to reach for instead of this one.
///     </para>
/// </remarks>
internal static class JbRunTimeout
{
    /// <summary>
    ///     The environment variable overriding <see cref="Default" />, read as a number of seconds.
    /// </summary>
    /// <remarks>
    ///     Named for this server rather than for <c>jb</c>: the <c>JB_</c> variables configure <c>jb</c> itself,
    ///     while this one is a kill timer <c>jb</c> never learns about. Seconds rather than minutes, so a cap can
    ///     be pitched just above a measured cold run.
    /// </remarks>
    internal const string Variable = "RESHARPER_MCP_TIMEOUT_SECS";

    /// <summary>The cap when <see cref="Variable" /> is unset, blank, or unreadable.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(600);

    /// <summary>
    ///     The smallest cap accepted. A smaller value is read as a mistake and raised, because a warm
    ///     whole-solution analysis alone costs the better part of a minute.
    /// </summary>
    internal static readonly TimeSpan Floor = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     The largest cap accepted: past a day the cap no longer bounds anything anyone is waiting for, and
    ///     bounding a hung <c>jb</c> is the reason to have one.
    /// </summary>
    internal static readonly TimeSpan Ceiling = TimeSpan.FromHours(24);

    /// <summary>
    ///     Returns the cap <paramref name="envValue" /> asks for, clamped between <see cref="Floor" /> and
    ///     <see cref="Ceiling" />.
    /// </summary>
    /// <remarks>
    ///     A value that is unparseable, non-finite, or not positive falls back to <see cref="Default" /> rather
    ///     than failing anything, matching how the server's other variables read: a typo costs the shipped
    ///     behaviour, never a broken server.
    /// </remarks>
    public static TimeSpan Resolve(string? envValue)
    {
        bool parsed = double.TryParse(envValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds);
        if (!parsed || !double.IsFinite(seconds) || seconds <= 0) return Default;

        // Clamp in seconds rather than building the TimeSpan first: a value like 1e300 overflows
        // TimeSpan.FromSeconds outright, and the clamp must not be the thing that throws.
        return TimeSpan.FromSeconds(Math.Clamp(seconds, Floor.TotalSeconds, Ceiling.TotalSeconds));
    }
}