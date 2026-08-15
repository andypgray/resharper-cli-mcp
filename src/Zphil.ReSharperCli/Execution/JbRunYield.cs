using Serilog;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Who outranks whom for the cache generation: a caller the user is waiting on always wins, and
///     speculative work either stands down or is taken off it. The third policy over a <c>jb</c> run,
///     beside <see cref="JbRunLock" /> — who may run at all — and <see cref="JbRunTimeout" /> — for how
///     long.
/// </summary>
/// <remarks>
///     <para>
///         Its own type rather than a rule inside the class that runs <c>jb</c>, because the precedence
///         belongs to every caller the user is waiting on and running <c>jb</c> is only what most of them
///         do. Written into the runner, it missed the one tool that spawns no process at all: a cache reset
///         queued behind a speculative pass for up to the whole run cap, where an inspect would have
///         reclaimed the generation in a second or two.
///     </para>
///     <para>
///         Process-wide rather than keyed per cache generation, so a caller against one solution stands a
///         pre-warm of another one down. That asymmetry predates this type and is preserved deliberately:
///         speculative work is worth so much less than a call in flight that telling the two generations
///         apart would cost more bookkeeping than it saves.
///     </para>
///     <para>
///         In-process only. A pre-warm running in another server process cannot be yielded to, and a call
///         there queues behind it exactly as it queues behind another session's real call.
///     </para>
///     <para>
///         <see cref="Interlocked" /> throughout, and no member waits on anything: cancellation callbacks
///         tree-kill a process inline on the canceller's thread, and that unwind ends in a
///         <see cref="JbRunLock" /> holder releasing a semaphore. A mutex held across the cancel would be a
///         third lock in that graph — neither bounded nor skippable, unlike the one
///         <c>CacheTransplanter</c> nests — which is the deadlock this codebase is careful not to build.
///     </para>
/// </remarks>
internal sealed class JbRunYield
{
    /// <summary>
    ///     How many callers the user is waiting on hold a claim right now. Two different reasons to stand a
    ///     pre-warm down, pointing the same way: a run analyses the whole solution into the same cache
    ///     generation a pre-warm would, so the speculative pass has nothing left to buy; a reset builds
    ///     nothing at all, and a pre-warm during one would rebuild exactly what the call exists to drop.
    ///     Starting one anyway is also the only way pre-warming could ever delay a call inside this process.
    ///     Reading this <em>after</em> publishing <see cref="_speculativeRun" /> is what closes the gap
    ///     between the two: whichever of the pair reads stale, the other has already seen the write it
    ///     needed.
    /// </summary>
    /// <remarks>
    ///     A count rather than the latch this used to be, and the difference is not bookkeeping. A latch
    ///     that is never cleared retires speculative work for the life of the process, so the moment it is
    ///     worth most — a foreground run has just hit the cap, the cache is part-built, the user is idle
    ///     reading an error saying a retry resumes from there — is exactly the moment the server has
    ///     guaranteed it will never run again. Clearing the latch on the way out instead would be wrong for
    ///     the opposite reason: with two callers overlapping, "the first one returned" is not "nobody is
    ///     waiting", and clearing on that first return opens the generation behind the second one's back.
    ///     Only a count says both things.
    /// </remarks>
    private int _foregroundCallers;

    /// <summary>
    ///     The speculative run in flight, or <see langword="null" />. Published so a caller the user is
    ///     waiting on can reclaim the cache generation instead of queueing behind work nobody asked for.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The published source is never disposed. A foreground caller that has already taken the
    ///         reference may be about to cancel it, and that window cannot be closed without holding a lock
    ///         across a cancellation whose callbacks tree-kill a process inline. One undisposed linked
    ///         source — one per speculative pass, not one per process — is the cheaper trade, and
    ///         <see cref="Reclaim" /> catches the disposal race regardless. Passes stay bounded because only
    ///         a foreground timeout starts one and no pass re-arms itself, so the total tracks what the user
    ///         did rather than a timer.
    ///     </para>
    ///     <para>
    ///         Cancelling is not instantaneous. The lease drops only after <see cref="ProcessRunner" /> sees
    ///         the cancellation, tree-kills <c>jb</c>, and reaps it, so a caller can still spend milliseconds
    ///         to a few seconds on the lock after cancelling. Bounded and far better than waiting out the
    ///         run, but yielding is not free.
    ///     </para>
    /// </remarks>
    private SpeculativeRun? _speculativeRun;

    /// <summary>
    ///     Count a caller the user is waiting on in, and take the cache generation back from any speculative
    ///     run holding it. Disposing the returned claim stands that caller back down.
    /// </summary>
    /// <remarks>
    ///     There is deliberately no way to reclaim without entering first. Cancelling the pass in flight
    ///     without counting yourself in leaves the door open behind you — the next pass to arrive is then
    ///     the one that delays the call — and making that unrepresentable rather than a convention two call
    ///     sites keep is most of the reason this is a type.
    /// </remarks>
    public IDisposable EnterForeground()
    {
        // Counted before the reclaim, and paired with the read in TryEnterSpeculative: from here on a
        // pre-warm either sees a non-zero count or has already published a claim for Reclaim to cancel.
        Interlocked.Increment(ref _foregroundCallers);

        Reclaim();

        return new ForegroundClaim(this);
    }

    /// <summary>
    ///     Claim the cache generation speculatively, or <see langword="null" /> when a caller the user is
    ///     waiting on is already in flight. The claim carries the token the speculative work must run
    ///     under — that is how it hears about being stood down — and withdraws itself when disposed.
    /// </summary>
    public SpeculativeRun? TryEnterSpeculative(CancellationToken cancellationToken)
    {
        // Publish before reading the count: a foreground caller that has already gone past its own reclaim
        // would find nothing to cancel, and would then queue behind a pass started a moment later.
        // Publish-then-check is what makes "a call is never delayed by a pre-warm in this process" a rule
        // rather than a near-certainty.
        SpeculativeRun mine = new(this, cancellationToken);
        Interlocked.Exchange(ref _speculativeRun, mine);

        if (Volatile.Read(ref _foregroundCallers) == 0) return mine;

        mine.Dispose();
        return null;
    }

    /// <summary>
    ///     Hand the cache generation to the caller: cancel the speculative run holding it, if any. The catch
    ///     is total rather than a list of the exceptions cancellation is known to raise, because the failure
    ///     mode it guards is not a noisy one — an escaping throw would leave the count raised with no claim
    ///     ever returned, silently retiring the pre-warm for the life of the process. Degrading to a queued
    ///     call is the behaviour without any of this, and a background optimisation must never be able to
    ///     fail one.
    /// </summary>
    private void Reclaim()
    {
        try
        {
            Interlocked.Exchange(ref _speculativeRun, null)?.Cancel();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not cancel the background cache pre-warm; this call will queue behind it instead");
        }
    }

    /// <summary>A speculative claim in flight: the token its work runs under, and its withdrawal.</summary>
    internal sealed class SpeculativeRun(JbRunYield owner, CancellationToken cancellationToken) : IDisposable
    {
        private readonly CancellationTokenSource _source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        /// <summary>What the speculative work runs under, so being stood down reaches it as a cancellation.</summary>
        public CancellationToken Token => _source.Token;

        /// <summary>
        ///     Withdraw this claim. Compare-and-swap on this instance, never a blind clear: by the time a
        ///     pass ends the field may already hold a <em>later</em> one, and a finished pre-warm must not
        ///     have its successor cancelled on its behalf. Deliberately leaves the source undisposed, for
        ///     the reason given on <see cref="_speculativeRun" />.
        /// </summary>
        public void Dispose()
        {
            Interlocked.CompareExchange(ref owner._speculativeRun, null, this);
        }

        /// <summary>
        ///     Stand this pass down. Reaching it means already holding the claim, and the only way to hold
        ///     another pass's claim is to take it out of <see cref="_speculativeRun" /> — which is private
        ///     to <see cref="JbRunYield" /> and read in exactly one place, after a caller has counted
        ///     itself in.
        /// </summary>
        internal void Cancel()
        {
            _source.Cancel();
        }
    }

    /// <summary>
    ///     One caller the user is waiting on, counted in. Releases once and only once — the shape
    ///     <c>JbRunLock.Holder</c> already uses — because a double dispose would drop the count below what
    ///     is in flight and let a pre-warm start behind a live call, which is the bug the count replaced a
    ///     latch to avoid.
    /// </summary>
    private sealed class ForegroundClaim(JbRunYield owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            Interlocked.Decrement(ref owner._foregroundCallers);
        }
    }
}