using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Identifies the run holding this server's slot, so a caller queueing behind it can be told what it is
///     waiting for.
/// </summary>
internal sealed record JbRunSlotHolder(string Subcommand, string SolutionPath);

/// <summary>
///     Limits this server to one <c>jb</c> process in flight at a time, whatever solutions the runs are
///     against.
/// </summary>
/// <remarks>
///     <para>
///         Process-wide rather than keyed per cache generation. <see cref="JbRunLock" /> partitions a
///         directory, because a directory is what two <c>jb</c> processes ruin between them, while this
///         partitions the machine, because a <c>jb</c> run is a multi-core analysis of a whole solution however
///         narrow its report. Two runs against different solutions pass the lock and share the machine rather
///         than the work: run together, each slows by multiples, enough to carry a one-file cleanup past the
///         default cap, while in sequence each finishes sooner.
///     </para>
///     <para>
///         The wait has no cap of its own. Every holder is a run of this process and so is already ended by the
///         run cap, which bounds the wait by the caller's own fan-out: a deep cold fan-out completes in arrival
///         order, where a cap here would fail its tail. The client's own cancellation ends the wait.
///     </para>
///     <para>
///         It reaches no further than this process: another session's server, or a <c>jb</c> started by hand,
///         runs beside it, so the cross-process half of the problem stays <see cref="JbRunLock" />'s.
///     </para>
/// </remarks>
internal sealed class JbRunSlot(ILogger<JbRunSlot> logger)
{
    /// <summary>
    ///     One at a time, on a <see cref="SemaphoreSlim" /> because it releases its async waiters in arrival
    ///     order, which makes a fan-out complete in the order it was issued.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    ///     Gets the run holding the slot, or <see langword="null" /> while it is free.
    /// </summary>
    /// <remarks>
    ///     Written under no lock: only the holder writes it, and it is read for a log line rather than for a
    ///     decision, so a read that races a handover names one of the two runs involved.
    /// </remarks>
    internal JbRunSlotHolder? Holder { get; private set; }

    /// <summary>
    ///     Waits for this server's <c>jb</c> slot, then returns the handle whose disposal releases it.
    /// </summary>
    /// <remarks>
    ///     The wait ends only when the slot is free or <paramref name="cancellationToken" /> is cancelled; the
    ///     class remarks say why it has no cap of its own.
    /// </remarks>
    public async Task<IDisposable> TakeAsync(string subcommand, string solutionPath, CancellationToken cancellationToken)
    {
        // Read before the wait, so the line below names the run this caller queued behind.
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
    ///     Answers <see langword="null" /> when another run of this server holds the slot. For speculative work,
    ///     which skips rather than queues, so a held slot is an ordinary answer; a caller the user is waiting on
    ///     uses <see cref="TakeAsync" />. Synchronous for the reason <see cref="JbRunLock.TryAcquire" /> is.
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