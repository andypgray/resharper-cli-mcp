using Microsoft.Extensions.Logging;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Decides who outranks whom for the cache generation: a caller the user is waiting on always wins, and
///     speculative work either stands down or is taken off it.
/// </summary>
/// <remarks>
///     <para>
///         A type of its own rather than a rule in the code that runs <c>jb</c>, because a caller that spawns
///         no process outranks a pre-warm too: a pre-warm beside a cache reset would rebuild what the reset
///         drops.
///     </para>
///     <para>
///         Process-wide rather than keyed per cache generation, for the reason <see cref="JbRunSlot" /> is: a
///         <c>jb</c> run occupies the machine, so a call on one solution stands a pre-warm of another down.
///         In-process only: a pre-warm in another server process cannot be reclaimed, and a call here queues
///         behind it as it would behind any other session's run.
///     </para>
///     <para>
///         <see cref="Interlocked" /> throughout, and no member waits on anything. Cancelling a pass can
///         tree-kill a process on the canceller's own thread, because the cancel may resume
///         <see cref="ProcessRunner" />'s awaited <c>WaitForExitAsync</c> inline, and the catch around it kills
///         the tree and unwinds into a <see cref="JbRunLock" /> holder releasing a semaphore. A lock held across
///         the cancel would add one to that graph that is neither bounded nor skippable, and so could deadlock.
///     </para>
/// </remarks>
internal sealed class JbRunYield(ILogger<JbRunYield> logger)
{
    /// <summary>
    ///     How many callers the user is waiting on hold a claim right now.
    /// </summary>
    /// <remarks>
    ///     A count rather than a flag, because waited-on callers overlap: speculative work has to be possible
    ///     again once the last of them leaves, and only a count knows when that is.
    /// </remarks>
    private int _foregroundCallers;

    /// <summary>
    ///     The speculative run in flight, published so a caller the user is waiting on can reclaim the cache
    ///     generation instead of queueing behind work nobody asked for.
    /// </summary>
    /// <remarks>
    ///     A published claim's token source is never disposed: a foreground caller that has taken the reference
    ///     may be about to cancel it, and closing that window would need a lock held across a cancellation that
    ///     can tree-kill a process on the caller's thread. That leaves one undisposed linked source per
    ///     speculative pass, which stays bounded as long as passes recur only on a foreground timeout, never on
    ///     a timer.
    /// </remarks>
    private SpeculativeRun? _speculativeRun;

    /// <summary>
    ///     Counts a caller the user is waiting on in, and takes the cache generation back from any speculative
    ///     run holding it.
    /// </summary>
    /// <remarks>
    ///     Disposing the returned claim counts that caller back out. There is no way to reclaim without entering
    ///     first, because a cancel without the count would let the next pass start and delay the call.
    /// </remarks>
    public IDisposable EnterForeground()
    {
        // Counted before the reclaim, and paired with the read in TryEnterSpeculative: from here on a
        // pre-warm either sees a non-zero count or has already published a claim for Reclaim to cancel.
        Interlocked.Increment(ref _foregroundCallers);

        Reclaim();

        // Stood down once and only once: a double dispose would drop the count below what is in flight and
        // let a pre-warm start behind a live call.
        return new ReleaseOnce(() => Interlocked.Decrement(ref _foregroundCallers));
    }

    /// <summary>
    ///     Claims the cache generation speculatively.
    /// </summary>
    /// <remarks>
    ///     Answers <see langword="null" /> when a caller the user is waiting on is already in flight. The claim
    ///     carries the token the speculative work must run under, which is how it hears it has been stood down,
    ///     and withdraws itself when disposed.
    /// </remarks>
    public SpeculativeRun? TryEnterSpeculative(CancellationToken cancellationToken)
    {
        // Publish before reading the count: a foreground caller that has already gone past its own reclaim
        // would find nothing to cancel, and would then queue behind a pass started a moment later. Publishing
        // first is what guarantees that a pre-warm never delays a call in this process.
        SpeculativeRun mine = new(this, cancellationToken);
        Interlocked.Exchange(ref _speculativeRun, mine);

        if (Volatile.Read(ref _foregroundCallers) == 0) return mine;

        mine.Dispose();

        // Debug, not Information: standing aside for a call the user made is the design working, and a
        // session that makes back-to-back calls would otherwise say so once per call for no reader's benefit.
        logger.LogDebug("Not starting speculative work: a call the user is waiting on is already in flight");
        return null;
    }

    /// <summary>
    ///     Hands the cache generation to the caller: cancels the speculative run holding it, if any.
    /// </summary>
    /// <remarks>
    ///     The catch is total rather than a list of the exceptions cancellation is known to raise, because an
    ///     escaping throw would leave the count raised with no claim ever returned, silently retiring the
    ///     pre-warm for the life of the process. Degrading to a queued call is the behaviour without any of
    ///     this, and a background optimisation must never be able to fail one.
    /// </remarks>
    private void Reclaim()
    {
        try
        {
            if (Interlocked.Exchange(ref _speculativeRun, null) is not { } speculative) return;

            speculative.Cancel();

            // Information, unlike the refusal above, because this one costs the caller time it cannot
            // otherwise account for: the lease drops only once the killed jb tree has been reaped, so the
            // call can spend seconds on the lock after this, and nothing else in the log would say why.
            logger.LogInformation(
                "Took the ReSharper cache generation back from the background pre-warm for a call the user is waiting on");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not cancel the background cache pre-warm; this call will queue behind it instead");
        }
    }

    /// <summary>Represents a speculative claim in flight: the token its work runs under, and its withdrawal.</summary>
    internal sealed class SpeculativeRun(JbRunYield owner, CancellationToken cancellationToken) : IDisposable
    {
        private readonly CancellationTokenSource _source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        /// <summary>
        ///     Gets the token the speculative work runs under, so being stood down reaches it as a cancellation.
        /// </summary>
        public CancellationToken Token => _source.Token;

        /// <summary>Withdraws this claim.</summary>
        /// <remarks>
        ///     Compare-and-swap on this instance, never a blind clear: by the time a pass ends the field may
        ///     already hold a later one, and clearing it would leave that pass impossible to reclaim. Leaves the
        ///     source undisposed, for the reason given on <see cref="_speculativeRun" />.
        /// </remarks>
        public void Dispose()
        {
            Interlocked.CompareExchange(ref owner._speculativeRun, null, this);
        }

        /// <summary>Stands this pass down.</summary>
        internal void Cancel()
        {
            _source.Cancel();
        }
    }
}