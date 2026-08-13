using System.Globalization;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     How long one <c>jb</c> run may take, and the environment variable that moves it. Resolved once at
///     the composition root and handed to both consumers — the run cap in <see cref="Services.JbRunner" />
///     and the queue wait in <see cref="JbRunLock" /> — so the two can never drift apart.
/// </summary>
/// <remarks>
///     <para>
///         The cap exists so a hung <c>jb</c> cannot occupy an agent indefinitely; it is not a statement
///         about how long analysis ought to take. Nothing outside this server imposes it — an MCP client
///         allows a tool call far longer, Claude Code's own limit being measured in hours — so this
///         number is the only thing that turns a slow call into a failed one, and every timeout a user
///         meets is one this server chose.
///     </para>
///     <para>
///         The default is sized for the run that actually approaches it: a cold whole-solution analysis.
///         A warm one finishes in well under a minute, and <c>--include</c> does not make a cold one any
///         cheaper — <c>jb</c> analyses the whole solution and narrows only what it inspects — so there
///         is no scoping lever to reach for instead of this one.
///     </para>
/// </remarks>
internal static class JbRunTimeout
{
    /// <summary>
    ///     Environment variable overriding <see cref="Default" />, read as a number of seconds. Named for
    ///     this server rather than for <c>jb</c> because that is whose cap it is: every <c>JB_</c> variable
    ///     the server reads becomes an argument <c>jb</c> itself sees, and this one becomes a kill timer
    ///     <c>jb</c> never learns about. Seconds rather than minutes because the interesting range is
    ///     narrow — the runs at issue take one to ten minutes — and whole minutes cannot express a cap
    ///     pitched just above a measured cold run.
    /// </summary>
    internal const string Variable = "RESHARPER_MCP_TIMEOUT_SECS";

    /// <summary>The cap when <see cref="Variable" /> is unset, blank, or unreadable.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(600);

    /// <summary>
    ///     A cap below this would kill runs that were never in trouble — a warm whole-solution analysis
    ///     alone costs the better part of a minute — so a smaller value is read as a mistake and raised.
    ///     Internal so the setup guide's clamp row is pinned against the real bound.
    /// </summary>
    internal static readonly TimeSpan Floor = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Past a day the cap has stopped bounding anything anyone is waiting for, and bounding a hung
    ///     <c>jb</c> is the whole reason to have one. Internal for the same reason as <see cref="Floor" />.
    /// </summary>
    internal static readonly TimeSpan Ceiling = TimeSpan.FromHours(24);

    /// <summary>
    ///     The cap <paramref name="envValue" /> asks for, clamped to <see cref="Floor" />..<see cref="Ceiling" />.
    ///     A value that is unparseable, non-finite, or not positive falls back to <see cref="Default" /> rather
    ///     than failing anything, matching how the server's other variables read: a typo costs the shipped
    ///     behaviour, never a broken server.
    /// </summary>
    public static TimeSpan Resolve(string? envValue)
    {
        bool parsed = double.TryParse(envValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds);
        if (!parsed || !double.IsFinite(seconds) || seconds <= 0) return Default;

        // Clamp in seconds rather than building the TimeSpan first: a value like 1e300 overflows
        // TimeSpan.FromSeconds outright, and the clamp must not be the thing that throws.
        return TimeSpan.FromSeconds(Math.Clamp(seconds, Floor.TotalSeconds, Ceiling.TotalSeconds));
    }
}