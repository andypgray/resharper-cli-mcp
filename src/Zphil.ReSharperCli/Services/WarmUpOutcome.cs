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
    ///     The pre-warm stood aside rather than fork a cold cache generation: either something already held
    ///     it, or a real call in this process reclaimed it mid-run. The two are one outcome because they are
    ///     one decision — the cache belongs to whoever actually needs it.
    /// </summary>
    Skipped,

    /// <summary>The pre-warm ran <c>jb</c> to a clean exit; the cache generation is warm.</summary>
    Warmed,

    /// <summary><c>jb</c> exited non-zero, or the pre-warm threw. The session simply pays the cold cost as before.</summary>
    Failed,

    /// <summary>The server shut down while the pre-warm was still working.</summary>
    Cancelled
}