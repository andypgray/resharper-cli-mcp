using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Formatting;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Everything one progress message is written from: the fixed facts of the run, where it has got to, and
///     how long it has been there.
/// </summary>
/// <param name="Subcommand">The <c>jb</c> subcommand this run is, e.g. <c>inspectcode</c>.</param>
/// <param name="SolutionPath">The solution being analysed.</param>
/// <param name="Phase">Where the run has got to.</param>
/// <param name="FilesSeen">How many files <c>jb</c> has named in this phase.</param>
/// <param name="CacheSummary">
///     What <see cref="JbCacheState" /> made of the cache in the moment before <c>jb</c> opened it, or
///     <see langword="null" /> while the run has not reached that moment. It is the single best predictor of
///     the minutes about to follow, which is why a message sent before <c>jb</c> has said anything carries it
///     rather than nothing.
/// </param>
/// <param name="Elapsed">
///     Time in the <c>jb</c> process when there is one, and time since the call arrived when there is not.
///     Two clocks rather than one because only the first is comparable to <see cref="Cap" />: queue time is
///     deliberately outside the run budget.
/// </param>
/// <param name="Cap">
///     The run cap, once armed — which is to say once <c>jb</c> has started. <see langword="null" /> before
///     that, because a queued call is not spending the run budget and a message saying otherwise would send a
///     caller to raise a cap that was never the problem.
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
    ///     Whether the run has only just arrived: still queued, for less than
    ///     <see cref="JbRunLock.NotableWait" />. The threshold is the lock's rather than one of this record's
    ///     own, and the judgement is made here beside the measurement rather than in the formatter, so
    ///     "has this caller genuinely queued behind someone" cannot answer differently between the log and
    ///     the progress message describing the same wait.
    /// </summary>
    internal bool JustArrived => Phase == JbRunPhase.Queued && Elapsed < JbRunLock.NotableWait;
}

/// <summary>
///     The advance of one <c>jb</c> run, reported on a timer. The fourth policy over a run, beside
///     <see cref="JbRunLock" /> — who may run — <see cref="JbRunYield" /> — who is made to wait — and
///     <see cref="JbRunTimeout" /> — for how long.
/// </summary>
/// <remarks>
///     <para>
///         <strong>The timer is the only thing that emits.</strong> <c>jb</c>'s own lines reach
///         <see cref="OnOutputLine" />, which does nothing but write fields the timer callback later reads.
///         Three things fall out of that one decision. Rate limiting and a heartbeat come from a single
///         moving part, so the interior gaps in <c>jb</c>'s output — measured at up to 42 seconds mid-stream
///         on a cold solution-wide run — are covered by the same mechanism that stops a cold run sending
///         1,332 notifications. The queue wait is covered too, which streaming <c>jb</c>'s output could never
///         do: <see cref="JbRunLock" />'s wait is bounded by the run cap, so a call can sit for the whole cap
///         before a process exists to stream. And a late line is harmless by construction —
///         <see cref="ProcessRunner" /> can abandon a live reader at the cap, so <see cref="OnOutputLine" />
///         may fire after the run it describes has already returned or thrown.
///     </para>
///     <para>
///         <strong>Nothing may emit after disposal.</strong> The sink ends at <c>TokenProgress.Report</c>,
///         which discards the <see cref="Task" /> from its send, so a report issued against a request that has
///         already been answered faults where only <c>TaskScheduler.UnobservedTaskException</c> would see it.
///         Hence <see cref="IAsyncDisposable" /> rather than <see cref="IDisposable" />:
///         <see cref="DisposeAsync" /> raises the disposed flag under the lock, which stops a beat that has
///         not started, and then awaits <see cref="Timer.DisposeAsync" />, which waits out one that has. A
///         synchronous <c>Dispose</c> can do the first but not the second.
///     </para>
///     <para>
///         Speculative work reports nothing and so never builds one of these: a pre-warm has no caller to
///         report to, and its whole contract is that it can neither delay nor fail a call.
///     </para>
/// </remarks>
internal sealed class JbRunProgress : IAsyncDisposable
{
    /// <summary>
    ///     How often a run in flight reports itself. The number carries no protocol meaning and is chosen for
    ///     legibility at both ends of the range this server sees: three or four messages across a 39-second
    ///     warm run, and about fifty across a 497-second cold one — against the 1,332 that streaming
    ///     <c>jb</c>'s per-file lines unfiltered would have sent.
    /// </summary>
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _cap;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Lock _gate = new();
    private readonly ILogger _logger;
    private readonly Action<JbRunProgressSnapshot> _report;
    private readonly string _solutionPath;
    private readonly string _subcommand;
    private readonly Timer _timer;

    private string? _cacheSummary;
    private bool _disposed;
    private int _filesSeen;
    private JbRunPhase _phase = JbRunPhase.Queued;

    /// <summary>
    ///     When <c>jb</c> started, measured on <see cref="_elapsed" />, or <see langword="null" /> while it
    ///     has not. Both halves of the two-clock rule come off this one field: it is what the reported elapsed
    ///     is taken from, and it is what says whether the run cap is armed and so worth naming.
    /// </summary>
    private TimeSpan? _spawnedAt;

    /// <param name="subcommand">The <c>jb</c> subcommand this run is.</param>
    /// <param name="solutionPath">The solution being analysed.</param>
    /// <param name="cap">The run cap, named in every message sent once <c>jb</c> is running.</param>
    /// <param name="report">
    ///     Where a heartbeat goes. Called from a timer thread, never under this class's lock, and never after
    ///     <see cref="DisposeAsync" /> has completed. It is expected to be prompt: disposal waits for a call
    ///     in flight, so a sink that blocked would hold up the tool call's own unwinding.
    /// </param>
    /// <param name="logger">
    ///     The caller's own, so a throwing sink leaves a line rather than vanishing. Required rather than
    ///     optional for the reason every other logger in this codebase is: a site that forgets it loses the
    ///     record silently, which is the class of defect the rule exists to prevent.
    /// </param>
    /// <param name="interval">
    ///     How often a heartbeat is sent, defaulting to <see cref="HeartbeatInterval" />. A parameter only so
    ///     a test need not wait ten seconds to see a second beat.
    /// </param>
    internal JbRunProgress(
        string subcommand,
        string solutionPath,
        TimeSpan cap,
        Action<JbRunProgressSnapshot> report,
        ILogger logger,
        TimeSpan? interval = null)
    {
        _subcommand = subcommand;
        _solutionPath = solutionPath;
        _cap = cap;
        _report = report;
        _logger = logger;

        // Last, and that is the whole reason this is a constructor body rather than a field initializer: the
        // first beat is immediate, and a callback reaching Snapshot() before the fields above were assigned
        // would report a half-built run. Immediate because the silence this exists to break starts at once —
        // the queue wait is the first thing a call does, and it runs up to the whole cap with no jb to stream.
        _timer = new Timer(_ => Beat(), null, TimeSpan.Zero, interval ?? HeartbeatInterval);
    }

    /// <summary>
    ///     How many files <c>jb</c> named in the phase it is in — for a caller restating how the run ended. A
    ///     run killed at the cap having analysed forty files and one killed at 1,200 are otherwise
    ///     indistinguishable, and the timeout message makes a claim about resuming that only this number
    ///     earns.
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
    ///     The heartbeat for one piece of work, or <see langword="null" /> when nobody asked for one — a
    ///     client that sent no progress token, or a caller with no channel at all. The one spelling of
    ///     "a snapshot becomes prose", so every caller that reports itself reads the same: a <c>jb</c> run,
    ///     and the cache reset that queues on the same <see cref="JbRunLock" /> without spawning anything.
    /// </summary>
    /// <remarks>
    ///     The adapter lives here rather than in each caller because <see cref="RunProgressFormatter" /> is
    ///     pure and everything above this handles strings, so neither the services nor the tool surface has
    ///     to know what a run's phases are called. A <see langword="null" /> sink answers
    ///     <see langword="null" /> rather than a reporter that drops its lines, which leaves a call site with
    ///     one nullable reporter to <c>await using</c> rather than a branch of its own.
    /// </remarks>
    internal static JbRunProgress? Reporting(
        string subcommand,
        string solutionPath,
        TimeSpan cap,
        Action<string>? onProgress,
        ILogger logger,
        TimeSpan? interval = null)
    {
        if (onProgress is null) return null;

        return new JbRunProgress(
            subcommand,
            solutionPath,
            cap,
            snapshot => onProgress(RunProgressFormatter.Format(snapshot)),
            logger,
            interval);
    }

    /// <summary>Enter the phase in which a sibling checkout's warm cache is copied into this one's.</summary>
    internal void Seeding()
    {
        lock (_gate)
        {
            if (_disposed) return;

            _phase = JbRunPhase.Seeding;
        }
    }

    /// <summary>
    ///     <c>jb</c> is about to start, with <paramref name="cacheSummary" /> describing what it will open.
    ///     This is also where the second clock starts and the run cap becomes worth naming.
    /// </summary>
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

    /// <summary>
    ///     Take in one line of <c>jb</c>'s standard output. Writes state and never emits, never throws, and
    ///     no-ops once disposed — see the class remarks for why all three are load-bearing rather than
    ///     defensive.
    /// </summary>
    internal void OnOutputLine(string line)
    {
        if (JbProgressLines.Classify(line) is not { } step) return;

        lock (_gate)
        {
            if (_disposed) return;

            // A phase change resets the count rather than carrying it: jb's two sweeps report different file
            // totals for the same solution — 1,332 analysed against 882 inspected on one measured run — so a
            // running total across both would be a number matching nothing jb ever said.
            if (_phase != step.Phase)
            {
                _phase = step.Phase;
                _filesSeen = 0;
            }

            if (step.NamesAFile) _filesSeen++;
        }
    }

    /// <summary>
    ///     One heartbeat: read the state, hand it to the sink outside the lock, and swallow whatever the sink
    ///     does with it.
    /// </summary>
    /// <remarks>
    ///     The catch is total, and for the reason <see cref="JbRunYield" />'s is: this runs on a timer thread,
    ///     where an escaping exception has no caller to reach and takes the process down instead. Reporting
    ///     progress is an optimisation over silence, and an optimisation may not fail — let alone end — a
    ///     call.
    /// </remarks>
    private void Beat()
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