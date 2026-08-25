namespace Zphil.ReSharperCli.Services;

/// <summary>
///     How a speculative <c>jb</c> run ended, which is one decision: did a <c>jb</c> start, and if it did,
///     what became of it. A pass that never spawned a process and a pass that spent minutes of multi-core
///     work before losing the cache generation are the same word only to a caller willing to report the
///     second as the first.
/// </summary>
/// <remarks>
///     Deliberately carries no <see cref="Execution.ProcessResult" />. Nothing downstream reads one —
///     <see cref="InspectService.WarmCacheAsync" /> discards the SARIF and <see cref="CacheWarmer" /> reads
///     only the exit code — and <see cref="JbRunner" />'s own spawn already owns the exit-code-zero
///     predicate, because that is what decides whether the warm marker is stamped. So naming
///     <see cref="Completed" /> apart from <see cref="Failed" /> here restates a decision already made
///     rather than adding one, and it leaves the caller a total switch instead of a null check.
/// </remarks>
internal enum SpeculativeRunOutcome
{
    /// <summary>
    ///     No <c>jb</c> was spawned: a caller the user is waiting on was already in flight, or the cache
    ///     generation was held. Nothing ran and nothing was spent.
    /// </summary>
    NotStarted,

    /// <summary><c>jb</c> exited zero; the cache generation is warm and the warm marker is stamped.</summary>
    Completed,

    /// <summary>
    ///     <c>jb</c> exited non-zero. Reported rather than thrown, because speculative work has no channel to
    ///     raise an error through and its caller is the one that decides what a failure means.
    /// </summary>
    Failed,

    /// <summary>
    ///     The run spent the whole run cap and had its process tree killed. Real work with no clean exit: no
    ///     warm marker is stamped, so nothing afterwards treats the generation as warm, but it is
    ///     substantially further along than it was.
    /// </summary>
    Capped,

    /// <summary>
    ///     A caller the user is waiting on took the cache generation back. It covers the two reclaims that
    ///     land before <c>jb</c> starts as well as the one that kills it mid-run, deliberately: a donor copy
    ///     can run for minutes before the pre-spawn guard is reached, and
    ///     <see cref="Execution.JbRunYield" /> records the reclaim at <c>Information</c> either way, so
    ///     reporting that nothing happened beside that line would be the contradiction this type exists to
    ///     remove.
    /// </summary>
    StoodDown
}