namespace Zphil.ReSharperCli.Services;

/// <summary>
///     How the last cache pre-warm pass ended. Product state rather than a test artefact — the log line the
///     warmer writes is derived from it — and none of these is an error: a pre-warm that did not happen
///     leaves the session exactly where it would have been without the feature. A pass that has settled
///     leaves its outcome standing until the next one settles, so this always names a real result.
/// </summary>
internal enum WarmUpOutcome
{
    /// <summary>No pass has been attempted yet — no client has connected, or one never will.</summary>
    NotRun,

    /// <summary>Turned off by the environment.</summary>
    Disabled,

    /// <summary>Nothing to warm: no <c>jb</c>, or no solution a call with no <c>solutionPath</c> would find.</summary>
    NoTarget,

    /// <summary>A run against this cache generation succeeded recently enough that warming it again would buy nothing.</summary>
    AlreadyWarm,

    /// <summary>
    ///     The pre-warm stood aside rather than fork a cold cache generation, without ever starting
    ///     <c>jb</c>: a call the user was waiting on was already in flight, or another process held the
    ///     generation. The two are one outcome because they are one decision — the cache belongs to whoever
    ///     actually needs it — and both cost nothing, which is what keeps this word honest.
    /// </summary>
    Skipped,

    /// <summary>The pre-warm ran <c>jb</c> to a clean exit; the cache generation is warm.</summary>
    Warmed,

    /// <summary>
    ///     The pass ran out the whole run cap and its <c>jb</c> was killed. No warm marker, so the next call
    ///     is not treated as warm, but the generation is substantially further along than it was — which is
    ///     the normal shape of a pre-warm on a large cold solution, and why it is not a
    ///     <see cref="Skipped" />.
    /// </summary>
    Capped,

    /// <summary><c>jb</c> exited non-zero, or the pre-warm threw. The session simply pays the cold cost as before.</summary>
    Failed,

    /// <summary>
    ///     A <c>jb</c> that was already working was stopped: the server shut down, or a caller the user was
    ///     waiting on took the cache generation back. One word for both because both are a running pass
    ///     cancelled from outside, and the log line naming the cancellation is written either way.
    /// </summary>
    Cancelled
}