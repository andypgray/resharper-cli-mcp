using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     The run holding this server's slot: which <c>jb</c> subcommand it is, and the solution it is against.
///     Named so a caller queueing behind it can be told what it is waiting for.
/// </summary>
internal sealed record JbRunSlotHolder(string Subcommand, string SolutionPath);

/// <summary>
///     How many <c>jb</c> processes this server may have in flight at once, which is one. The fifth policy
///     over a run, beside <see cref="JbRunLock" /> — who may run on a cache generation —
///     <see cref="JbRunYield" /> — who outranks whom — <see cref="JbRunTimeout" /> — for how long — and
///     <see cref="JbRunProgress" /> — how it reports itself.
/// </summary>
/// <remarks>
///     <para>
///         Process-wide rather than keyed per cache generation, for the reason <see cref="JbRunYield" />'s
///         remarks already give for the precedence being process-wide: the lock partitions a
///         <em>directory</em>, because a directory is what two <c>jb</c> processes ruin between them, while
///         this partitions the <em>machine</em>, because a <c>jb</c> run is a full multi-core analysis of a
///         whole solution whatever the report is narrowed to. Two runs against different solutions contend
///         for nothing the lock can see and share the machine rather than the work. Measured on one machine
///         on 2026-09-04: four cleanups issued a second apart against four solutions ran 367 s, 413 s,
///         476 s and 616 s, where three of them were one-file cleanups that take 24–50 s alone and the
///         slowest crossed the 600-second default cap. In sequence every one of them would have finished
///         sooner.
///     </para>
///     <para>
///         The wait has no cap of its own, unlike every other policy here. Every holder is a run of
///         <em>this</em> process and so is already ended by the run cap, which makes the wait bounded by the
///         caller's own fan-out: a deep cold fan-out completes in arrival order rather than failing at the
///         tail. A cap here would fail the calls a client issued together, which is the shape this exists to
///         make survivable. The client's own cancellation ends the wait, and
///         <see cref="JbRunProgress" /> reports it every ten seconds so a caller can see what it is behind.
///     </para>
///     <para>
///         Two limits. It reaches no further than this process — another session's server, and a <c>jb</c>
///         you start yourself, run beside it — which is why the cross-process half of the problem stays
///         <see cref="JbRunLock" />'s. And <c>CacheResetService</c> deliberately does not take it: it spawns
///         nothing and its deletes take moments, so charging it a slot would make it queue behind minutes of
///         analysis for no machine it was going to contend for.
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
    ///     Wait for this server's <c>jb</c> slot, then return the handle whose disposal releases it. The
    ///     wait ends only when the slot is free or <paramref name="cancellationToken" /> is cancelled — see
    ///     the class remarks for why there is no cap of its own.
    /// </summary>
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
    ///     Take the slot if it is free right now, or <see langword="null" /> when another run of this server
    ///     holds it. For speculative work only — a caller the user is waiting on uses
    ///     <see cref="TakeAsync" />.
    /// </summary>
    /// <remarks>
    ///     Synchronous for the reason <see cref="JbRunLock.TryAcquire" /> is: every step of a zero-wait
    ///     acquire is, and an <c>async</c> signature would promise a wait that must never happen. Speculative
    ///     work skips rather than queues, so a held slot is an ordinary answer and not a delay.
    /// </remarks>
    public IDisposable? TryTake(string subcommand, string solutionPath)
    {
        if (!_gate.Wait(0)) return null;

        return Hold(subcommand, solutionPath);
    }

    /// <summary>
    ///     Record who holds the slot just taken, and hand back the handle that gives it up. It releases once
    ///     and only once, because a double dispose would over-release the semaphore and admit a second
    ///     <c>jb</c>, which is the one thing this class exists to prevent.
    /// </summary>
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
    ///     Say how long this caller queued for the slot, and behind what. The threshold is
    ///     <see cref="JbRunLock.NotableWait" /> rather than a second constant: both are the same judgement —
    ///     has this caller genuinely queued behind someone — and an answer that could differ between them
    ///     would make two lines about one wait disagree.
    /// </summary>
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