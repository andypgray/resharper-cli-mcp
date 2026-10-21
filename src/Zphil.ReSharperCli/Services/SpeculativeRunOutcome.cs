namespace Zphil.ReSharperCli.Services;

/// <summary>
///     Defines how a speculative <c>jb</c> run ended: whether a <c>jb</c> started, and if it did, what became
///     of it.
/// </summary>
/// <remarks>
///     <para>
///         A pass that never spawned a process and one that spent minutes of multi-core work before losing the
///         cache generation are different endings, and a caller summarising a pass must not report the second
///         as the first: the run lines underneath its summary already say which it was.
///     </para>
///     <para>
///         It carries no <see cref="Execution.ProcessResult" />: <see cref="JbRunner" />'s spawn owns the
///         exit-code-zero predicate that decides whether the warm marker is stamped, so naming
///         <see cref="Completed" /> apart from <see cref="Failed" /> restates that decision rather than letting
///         a caller make it again, and leaves the caller a total switch instead of a null check.
///     </para>
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
    ///     A caller the user is waiting on took the cache generation back.
    /// </summary>
    /// <remarks>
    ///     It covers a reclaim that lands before <c>jb</c> starts as well as one that kills it mid-run: a donor
    ///     copy can run for minutes before the pre-spawn guard is reached, and
    ///     <see cref="Execution.JbRunYield" /> records the reclaim at <c>Information</c> either way, so reporting
    ///     that nothing happened beside that line would contradict it.
    /// </remarks>
    StoodDown
}