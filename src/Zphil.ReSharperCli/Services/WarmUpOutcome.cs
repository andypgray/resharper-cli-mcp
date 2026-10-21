namespace Zphil.ReSharperCli.Services;

/// <summary>
///     Defines how the last cache pre-warm pass ended.
/// </summary>
/// <remarks>
///     None of these is an error: a pre-warm that did not happen leaves the session where it would have been
///     without the feature.
/// </remarks>
internal enum WarmUpOutcome
{
    /// <summary>No pass has been attempted yet — no client has connected, or one never will.</summary>
    NotRun,

    /// <summary>Turned off by the environment.</summary>
    Disabled,

    /// <summary>Nothing to warm: no <c>jb</c>, or no solution a call with no <c>solutionPath</c> would find.</summary>
    NoTarget,

    /// <summary>
    ///     A run against this cache generation succeeded recently enough that warming it again would buy
    ///     nothing. A recent run by another <c>jb</c> build is not enough, because <c>jb</c> rebuilds a cache
    ///     another build wrote.
    /// </summary>
    AlreadyWarm,

    /// <summary>
    ///     The pre-warm stood aside without starting <c>jb</c>, rather than fork a cold cache generation: a call
    ///     the user was waiting on was already in flight, or another process held the generation. These are one
    ///     outcome because they are one decision, that the cache belongs to whoever needs it, and none of them
    ///     cost anything.
    /// </summary>
    Skipped,

    /// <summary>The pre-warm ran <c>jb</c> to a clean exit; the cache generation is warm.</summary>
    Warmed,

    /// <summary>
    ///     The pass ran out the whole run cap and its <c>jb</c> was killed. No warm marker, so the next call
    ///     is not treated as warm, but the generation is substantially further along than it was: the normal
    ///     shape of a pre-warm on a large cold solution, and why it is not a <see cref="Skipped" />.
    /// </summary>
    Capped,

    /// <summary>
    ///     <c>jb</c> exited non-zero, or the pre-warm threw. The session pays the cold cost it would have paid
    ///     without one.
    /// </summary>
    Failed,

    /// <summary>
    ///     The pass was cancelled from outside, before <c>jb</c> started or while it ran: the server shut down,
    ///     or a caller the user was waiting on took the cache generation back.
    /// </summary>
    Cancelled
}