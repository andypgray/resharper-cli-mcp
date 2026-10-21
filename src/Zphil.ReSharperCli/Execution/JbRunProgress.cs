using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Formatting;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Represents everything one progress message is written from: the fixed facts of the run, where it has got
///     to, and how long it has been there.
/// </summary>
/// <param name="Subcommand">The <c>jb</c> subcommand this run is, e.g. <c>inspectcode</c>.</param>
/// <param name="SolutionPath">The solution being analysed.</param>
/// <param name="Phase">Where the run has got to.</param>
/// <param name="FilesSeen">How many files <c>jb</c> has named in this phase.</param>
/// <param name="CacheSummary">
///     What <see cref="JbCacheState" /> made of the cache just before <c>jb</c> opened it, or
///     <see langword="null" /> before the run reaches that point.
/// </param>
/// <param name="Elapsed">
///     Time in the <c>jb</c> process once there is one, and time since the call arrived before that. Only the
///     first is comparable to <see cref="Cap" />, because queue time is outside the run budget.
/// </param>
/// <param name="Cap">
///     The run cap once <c>jb</c> has started, and <see langword="null" /> before then, because a queued call is
///     not spending the run budget.
/// </param>
internal sealed record JbRunProgressSnapshot(
    string Subcommand,
    string SolutionPath,
    JbRunPhase Phase,
    int FilesSeen,
    string? CacheSummary,
    TimeSpan Elapsed,
    TimeSpan? Cap)
{
    /// <summary>
    ///     Gets a value indicating whether the run has only just arrived: still waiting in a queue before
    ///     <c>jb</c> exists, for less than <see cref="JbRunLock.NotableWait" />.
    /// </summary>
    /// <remarks>
    ///     <see cref="JbRunPhase.Turn" /> counts as well as <see cref="JbRunPhase.Queued" />, because the first
    ///     beat is immediate and lands before either wait has established anything, so naming a run ahead of the
    ///     caller would be a claim nothing has made.
    /// </remarks>
    internal bool JustArrived =>
        Elapsed < JbRunLock.NotableWait && Phase is JbRunPhase.Turn or JbRunPhase.Queued;
}

/// <summary>
///     Reports the advance of one <c>jb</c> run on a timer.
/// </summary>
/// <remarks>
///     <para>
///         The timer is the only thing that emits, one beat at a time. <c>jb</c>'s own lines reach
///         <see cref="OnOutputLine" />, which only writes fields the timer callback later reads. So one mechanism
///         is both the rate limit and the heartbeat, covering the gaps in <c>jb</c>'s output and the queue wait
///         before any process exists, and a line that arrives after the run has returned or thrown is harmless;
///         <see cref="ProcessRunner" /> can deliver one, since it abandons a live reader at the cap.
///         <see cref="Timer" /> does not serialize its callbacks, so <see cref="Beat" /> carries a guard of its own.
///     </para>
///     <para>
///         Nothing emits once <see cref="DisposeAsync" /> has completed, since a beat after the call has been
///         answered has no frame it may write. That is why this is <see cref="IAsyncDisposable" />: only an
///         awaited <see cref="Timer.DisposeAsync" /> waits out a beat already in flight.
///     </para>
/// </remarks>
internal sealed class JbRunProgress : IAsyncDisposable
{
    /// <summary>How often a run in flight reports itself.</summary>
    /// <remarks>
    ///     Chosen for legibility rather than any protocol meaning: a few messages across a warm run, and a few
    ///     dozen across a cold one of several minutes, where streaming <c>jb</c>'s lines would send one per file.
    /// </remarks>
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _cap;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Lock _gate = new();
    private readonly ILogger _logger;
    private readonly Action<JbRunProgressSnapshot> _report;
    private readonly string _solutionPath;
    private readonly string _subcommand;
    private readonly Timer _timer;

    /// <summary>Set while a beat runs; <see cref="Beat" /> says why this is not <see cref="_gate" />.</summary>
    private int _beating;

    private string? _cacheSummary;
    private bool _disposed;
    private int _filesSeen;
    private JbRunPhase _phase;

    /// <summary>
    ///     When <c>jb</c> started, measured on <see cref="_elapsed" />, or <see langword="null" /> before then.
    ///     Both clocks come off it: the reported elapsed is measured from it, and its presence is what says the
    ///     run cap is armed.
    /// </summary>
    private TimeSpan? _spawnedAt;

    /// <summary>
    ///     Starts reporting a run of <paramref name="subcommand" /> on <paramref name="solutionPath" /> under the
    ///     run cap <paramref name="cap" />, sending the first beat at once.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <paramref name="report" /> is called from a timer thread, never under this class's lock, never two
    ///         at once, and never after <see cref="DisposeAsync" /> has completed. It has to be prompt: disposal
    ///         waits for a call in flight, so a sink that blocked would hold up the tool call's own unwinding.
    ///         <paramref name="logger" /> is the caller's, so a throwing sink leaves a line rather than vanishing.
    ///     </para>
    ///     <para>
    ///         <paramref name="initialPhase" /> is the wait the run opens in: <see cref="JbRunPhase.Turn" /> for a
    ///         run that queues for <see cref="JbRunSlot" /> first, <see cref="JbRunPhase.Queued" /> for one that
    ///         goes straight to <see cref="JbRunLock" />. It is required rather than defaulted because the first
    ///         beat reports it. <paramref name="interval" /> defaults to <see cref="HeartbeatInterval" />, and is a
    ///         parameter so a test need not wait a production interval to see a second beat.
    ///     </para>
    /// </remarks>
    internal JbRunProgress(
        string subcommand,
        string solutionPath,
        TimeSpan cap,
        Action<JbRunProgressSnapshot> report,
        ILogger logger,
        JbRunPhase initialPhase,
        TimeSpan? interval = null)
    {
        _subcommand = subcommand;
        _solutionPath = solutionPath;
        _cap = cap;
        _report = report;
        _logger = logger;
        _phase = initialPhase;

        // Created last, which is why this is a constructor body rather than field initializers: the first beat
        // is immediate, and a callback reaching Snapshot() before the fields above were assigned would report a
        // half-built run. Immediate because the queue wait is the first thing a call does, and it can last the
        // whole cap with no jb to stream.
        _timer = new Timer(_ => Beat(), null, TimeSpan.Zero, interval ?? HeartbeatInterval);
    }

    /// <summary>
    ///     Gets how many files <c>jb</c> has named in the phase it is in.
    /// </summary>
    internal int FilesSeen
    {
        get
        {
            lock (_gate)
            {
                return _filesSeen;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;
        }

        // Stops a beat that has not started (the flag above) and waits out one that has (this await). Both
        // halves are needed: the flag alone leaves an in-flight callback free to report against a request
        // that has already been answered.
        await _timer.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Returns a reporter that formats each snapshot with <see cref="RunProgressFormatter" /> for
    ///     <paramref name="onProgress" />, or <see langword="null" /> when there is no sink.
    /// </summary>
    /// <remarks>
    ///     Formatting here leaves every caller handling strings, so none of them needs to know what a run's
    ///     phases are called.
    /// </remarks>
    internal static JbRunProgress? Reporting(
        string subcommand,
        string solutionPath,
        TimeSpan cap,
        Action<string>? onProgress,
        ILogger logger,
        JbRunPhase initialPhase,
        TimeSpan? interval = null)
    {
        if (onProgress is null) return null;

        return new JbRunProgress(
            subcommand,
            solutionPath,
            cap,
            snapshot => onProgress(RunProgressFormatter.Format(snapshot)),
            logger,
            initialPhase,
            interval);
    }

    /// <summary>
    ///     Enters the phase in which this server's <c>jb</c> slot is held and the cache generation's lease is
    ///     being queued for.
    /// </summary>
    internal void Queued()
    {
        Enter(JbRunPhase.Queued);
    }

    /// <summary>Enters the phase in which a sibling checkout's warm cache is copied into this one's.</summary>
    internal void Seeding()
    {
        Enter(JbRunPhase.Seeding);
    }

    /// <summary>
    ///     Moves to <paramref name="phase" /> unless disposed.
    /// </summary>
    private void Enter(JbRunPhase phase)
    {
        lock (_gate)
        {
            if (_disposed) return;

            _phase = phase;
        }
    }

    /// <summary>
    ///     Records that <c>jb</c> is about to start, with <paramref name="cacheSummary" /> describing what it
    ///     will open.
    /// </summary>
    /// <remarks>From here a snapshot's elapsed time is time in the process, and its cap is set.</remarks>
    internal void Spawning(string cacheSummary)
    {
        lock (_gate)
        {
            if (_disposed) return;

            _phase = JbRunPhase.Starting;
            _cacheSummary = cacheSummary;
            _spawnedAt = _elapsed.Elapsed;
        }
    }

    /// <summary>Takes in one line of <c>jb</c>'s standard output.</summary>
    /// <remarks>
    ///     Writes state and nothing else: it never emits, never throws, and does nothing once disposed, because it
    ///     runs on the process reader thread and can run after the run it describes has returned.
    /// </remarks>
    internal void OnOutputLine(string line)
    {
        if (JbProgressLines.Classify(line) is not { } step) return;

        lock (_gate)
        {
            if (_disposed) return;

            // A phase change resets the count rather than carrying it: jb's analysis and inspection sweeps
            // report different file totals for the same solution, so a running total across both would match
            // nothing jb ever said.
            if (_phase != step.Phase)
            {
                _phase = step.Phase;
                _filesSeen = 0;
            }

            if (step.NamesAFile) _filesSeen++;
        }
    }

    /// <summary>
    ///     Emits one beat, skipping it while another is still going out.
    /// </summary>
    /// <remarks>
    ///     <see cref="Timer" /> does not serialize its callbacks, so under a starved thread pool two beats can run
    ///     at once, snapshot in one order and reach the sink in the other, naming an earlier elapsed than the
    ///     message before; a pool recovering from a stall would also deliver the whole burst it queued. Skipping
    ///     loses nothing, since the skipped beat would report the same state milliseconds later. The guard is a
    ///     flag rather than <see cref="_gate" /> held across <see cref="_report" />, because
    ///     <see cref="OnOutputLine" /> takes that lock on the process reader thread, and a slow sink must not stall
    ///     <c>jb</c>'s stdout drain. A sink that serializes its own writes does not make this redundant: it orders
    ///     sends, and this orders snapshots.
    /// </remarks>
    private void Beat()
    {
        if (Interlocked.Exchange(ref _beating, 1) == 1) return;

        try
        {
            Emit();
        }
        finally
        {
            Volatile.Write(ref _beating, 0);
        }
    }

    /// <summary>
    ///     Sends one heartbeat: reads the state under the lock, hands it to the sink outside it, and swallows
    ///     whatever the sink throws.
    /// </summary>
    /// <remarks>
    ///     The catch is total: this runs on a timer thread, where an escaping exception has no caller to reach
    ///     and takes the process down instead. Reporting progress is an optimisation over silence, and an
    ///     optimisation must not fail a call, let alone end the process.
    /// </remarks>
    private void Emit()
    {
        JbRunProgressSnapshot snapshot;
        lock (_gate)
        {
            if (_disposed) return;

            snapshot = Snapshot();
        }

        try
        {
            _report(snapshot);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "Could not report the progress of the jb {Subcommand} run on {SolutionPath}",
                _subcommand,
                _solutionPath);
        }
    }

    private JbRunProgressSnapshot Snapshot()
    {
        TimeSpan total = _elapsed.Elapsed;

        return new JbRunProgressSnapshot(
            _subcommand,
            _solutionPath,
            _phase,
            _filesSeen,
            _cacheSummary,
            _spawnedAt is { } spawned ? total - spawned : total,
            _spawnedAt is null ? null : _cap);
    }
}