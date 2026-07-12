namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     Controls how much detail a formatted response carries during progressive detail reduction. When the
///     output at one level exceeds the character budget, <see cref="ProgressiveRenderer" /> retries at the
///     next (lower) level until it fits. The values are ordered most-to-least detail; their exact meaning is
///     defined per formatter (see <see cref="CleanupSummaryFormatter" />).
/// </summary>
internal enum DetailLevel
{
    /// <summary>Full detail: every item listed individually.</summary>
    Full = 0,

    /// <summary>High detail: most content retained, the largest low-signal category collapsed to a count.</summary>
    High = 1,

    /// <summary>Medium detail: secondary categories also collapsed to counts.</summary>
    Medium = 2,

    /// <summary>Low detail: only the highest-signal items listed; everything else as counts.</summary>
    Low = 3,

    /// <summary>Minimal detail: one-line summary, every category as a count.</summary>
    Minimal = 4
}