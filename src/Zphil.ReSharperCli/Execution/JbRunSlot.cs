using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     The run holding this server's slot, named so a caller queueing behind it can be told what it is
///     waiting for.
/// </summary>
internal sealed record JbRunSlotHolder(string Subcommand, string SolutionPath);

/// <summary>
///     How many <c>jb</c> processes this server may have in flight at once, which is one.
/// </summary>
/// <remarks>
///     <para>
///         Process-wide rather than keyed per cache generation. <see cref="JbRunLock" /> partitions a
///         <em>directory</em>, because a directory is what two <c>jb</c> processes ruin between them, while
///         this partitions the <em>machine</em>, because a <c>jb</c> run is a full multi-core analysis of a
///         whole solution whatever the report is narrowed to. Two runs against different solutions contend
///         for nothing the lock can see and share the machine rather than the work: run together, each slows
///         by multiples, enough to carry a one-file cleanup past the default cap, where in sequence every one
///         of them finishes sooner.
///     </para>
///     <para>
///         The wait has no cap of its own. Every holder is a run of <em>this</em> process and so is already
///         ended by the run cap, which makes the wait bounded by the caller's own fan-out: a deep cold fan-out
///         completes in arrival order rather than failing at the tail. A cap here would fail the calls a client
///         issued together, which is the shape this exists to make survivable. The client's own cancellation
///         ends the wait, and <see cref="JbRunProgress" /> reports it while it lasts, so a caller can see what
///         it is behind.
///     </para>
///     <para>
///         It reaches no further than this process — another session's server, and a <c>jb</c> you start
///         yourself, run beside it — which is why the cross-process half of the problem stays
///         <see cref="JbRunLock" />'s.
///     </para>
/// </remarks>
internal sealed class JbRunSlot(ILogger<JbRunSlot> logger)
{
    /// <summary>
    ///     One at a time, and <see cref="SemaphoreSlim" /> because its async waiters are released in arrival
    ///     order — which is what makes a fan-out complete in the order it was issued rather than in whatever
    ///     order the thread pool wakes.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    ///     Who holds the slot right now, or <see langword="null" /> when nobody does. Written under no lock:
    ///     only the holder writes it, and it is read for a log line rather than for a decision, so a read
    ///     that races a handover names one of the two runs genuinely involved.
    /// </summary>
    internal JbRunSlotHolder? Holder { get; private set; }

    /// <summary>
    ///     Waits for this server's <c>jb</c> slot, then returns the handle whose disposal releases it.
    /// </summary>
    /// <remarks>
    ///     The wait ends only when the slot is free or <paramref name="cancellationToken" /> is cancelled — see
    ///     the class remarks for why there is no cap of its own.
    /// </remarks>
    public async Task<IDisposable> TakeAsync(string subcommand, string solutionPath, CancellationToken cancellationToken)
    {
        // Read before the wait, so the line below names the run this caller actually queued behind rather
        // than whichever one happened to hold the slot as it got in.
        JbRunSlotHolder? ahead = Holder;
        var waited = Stopwatch.StartNew();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        IDisposable slot = Hold(subcommand, solutionPath);
        Report(waited.Elapsed, ahead);

        return slot;
    }

    /// <summary>
    ///     Takes the slot if it is free right now, without waiting.
    /// </summary>
    /// <remarks>
    ///     Answers <see langword="null" /> when another run of this server holds it. For speculative work
    ///     only — a caller the user is waiting on uses <see cref="TakeAsync" />. Speculative work skips rather
    ///     than queues, so a held slot is an ordinary answer and not a delay. Synchronous for the reason
    ///     <see cref="JbRunLock.TryAcquire" /> is: every step of a zero-wait acquire is, and an <c>async</c>
    ///     signature would promise a wait that must never happen.
    /// </remarks>
    public IDisposable? TryTake(string subcommand, string solutionPath)
    {
        if (!_gate.Wait(0)) return null;

        return Hold(subcommand, solutionPath);
    }

    /// <summary>
    ///     Records who holds the slot just taken, and hands back the handle that gives it up.
    /// </summary>
    /// <remarks>
    ///     The handle releases once and only once, because a double dispose would over-release the semaphore
    ///     and admit a second <c>jb</c>.
    /// </remarks>
    private IDisposable Hold(string subcommand, string solutionPath)
    {
        Holder = new JbRunSlotHolder(subcommand, solutionPath);

        return new ReleaseOnce(() =>
        {
            Holder = null;
            _gate.Release();
        });
    }

    /// <summary>
    ///     Logs how long this caller queued for the slot, and behind what.
    /// </summary>
    /// <remarks>
    ///     The threshold is <see cref="JbRunLock.NotableWait" /> rather than a second constant: both are the
    ///     same judgement — has this caller genuinely queued behind someone — and two thresholds could make the
    ///     slot's line and the lock's line disagree about one caller's queueing.
    /// </remarks>
    private void Report(TimeSpan waited, JbRunSlotHolder? ahead)
    {
        if (waited >= JbRunLock.NotableWait)
        {
            logger.LogInformation(
                "Waited {WaitedMs} ms for this server's jb slot behind {HolderSubcommand} on {HolderSolutionPath}",
                (long)waited.TotalMilliseconds,
                ahead?.Subcommand,
                ahead?.SolutionPath);

            return;
        }

        logger.LogDebug("Took this server's jb slot after {WaitedMs} ms", (long)waited.TotalMilliseconds);
    }
}