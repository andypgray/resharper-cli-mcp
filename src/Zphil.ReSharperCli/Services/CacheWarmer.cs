using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Services;

/// <summary>
///     Runs the server's one kind of background work: populating the ReSharper cache generation a tool call is
///     going to want, in an idle window rather than in the user's.
/// </summary>
/// <remarks>
///     <para>
///         At most one pass is in flight, and a pass recurs only when a foreground run hits the cap: never on a
///         timer, per message, or from a pass's own timeout.
///     </para>
///     <para>
///         It decides when a warm-up runs; <see cref="InspectService.WarmCacheAsync" /> decides how, so no
///         <c>jb</c> argument is built here. The target is whatever <c>ConfigResolver.ResolveAsync</c> resolves
///         with no override, the solution a call with no <c>solutionPath</c> would use, rather than a discovery
///         rule of its own.
///     </para>
///     <para>
///         Speculative work must never be able to fail, delay, or report through a tool call, so every path
///         here ends in a <see cref="WarmUpOutcome" /> and nothing propagates. Only an unexpected exception is a
///         warning.
///     </para>
///     <para>
///         It is an <see cref="IHostedService" /> so that <see cref="StopAsync" /> can drain a pass in flight:
///         cancelling alone would kill <c>jb</c> without waiting for it, and a <c>jb</c> outliving this process
///         keeps ReSharper's own cache-generation lock after the OS has dropped our lock-file handle. The drain
///         covers an orderly shutdown only. A server killed outright never runs it, which is what
///         <see cref="ChildProcessLifetime" /> covers, unequally by platform and not at all on macOS, so on macOS
///         the drain is the only protection there is.
///     </para>
/// </remarks>
internal sealed class CacheWarmer(
    ConfigResolver configResolver,
    InspectService inspectService,
    JbRunner jbRunner,
    IEnvironment environment,
    ILogger<CacheWarmer> logger) : IHostedService, IDisposable
{
    /// <summary>Environment variable that turns the pre-warm off. Documented spelling: <c>off</c>.</summary>
    internal const string EnableVariable = "RESHARPER_MCP_PREWARM";

    /// <summary>
    ///     How recently a <c>jb</c> run must have succeeded against a cache generation for a pre-warm to skip
    ///     it, provided that run was by the build this pass would start (<see cref="WarmedRecentlyByThisBuild" />).
    /// </summary>
    /// <remarks>
    ///     Errs long on purpose: a skipped pre-warm costs nothing a session without one would not pay, while a
    ///     needless one costs a couple of minutes of multi-core CPU. Without it, a user-scope server would
    ///     analyse a solution at every session start in every C# repo.
    /// </remarks>
    internal static readonly TimeSpan RecentlyWarmWindow = TimeSpan.FromHours(1);

    /// <summary>
    ///     Long enough for a killed <c>jb</c> tree to be reaped within
    ///     <see cref="ProcessRunner.KilledTreeReapBudget" />, and not much longer.
    /// </summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Guards <see cref="_finished" />, <see cref="_passInFlight" /> and <see cref="_stopped" /> as one.
    /// </summary>
    /// <remarks>
    ///     <see cref="Start" /> claims the slot and republishes the completion signal in one step, and
    ///     <see cref="StopAsync" /> closes the door and reads that signal in one step. Interleaved without the
    ///     gate, <c>Start</c> claims the slot, <c>StopAsync</c> reads the previous pass's already-settled task,
    ///     drains at once and returns, and <c>Start</c> then spawns a <c>jb</c> after shutdown.
    /// </remarks>
    private readonly Lock _gate = new();

    private readonly CancellationTokenSource _stopping = new();

    private TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _passInFlight;
    private bool _stopped;

    /// <summary>
    ///     Gets how the last settled pass ended, <see cref="WarmUpOutcome.NotRun" /> until one has.
    /// </summary>
    /// <remarks>
    ///     Never reset when a pass begins, so it always names a real outcome rather than briefly reading as
    ///     "not run".
    /// </remarks>
    // A seam: a pass reports to no caller, so this is how a test learns how one ended without reading its log line.
    internal WarmUpOutcome Outcome { get; private set; } = WarmUpOutcome.NotRun;

    /// <summary>
    ///     Gets a task that completes once the pass in flight has settled, on every path including the ones
    ///     that never ran <c>jb</c>.
    /// </summary>
    /// <remarks>
    ///     Republished when a pass starts, so a caller awaiting it after a re-arm waits for that pass rather
    ///     than sailing past on the previous one's completed task.
    /// </remarks>
    // A seam: a pass is fire-and-forget, so awaiting this is how a test sees one settle rather than sleeping past it.
    // StopAsync drains through the completion source itself.
    internal Task Finished
    {
        get
        {
            lock (_gate)
            {
                return _finished.Task;
            }
        }
    }

    public void Dispose()
    {
        _stopping.Dispose();
    }

    /// <summary>
    ///     Subscribes to <see cref="JbRunner.ForegroundRunTimedOut" />, so a foreground run hitting the cap
    ///     arranges a pass, and starts none itself.
    /// </summary>
    /// <remarks>
    ///     The host starts before any client has connected, so there is nothing to warm yet;
    ///     <see cref="Start" /> begins a pass.
    /// </remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        jbRunner.ForegroundRunTimedOut += OnForegroundRunTimedOut;
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Cancels a pre-warm in flight and waits for it to let go, so the process never leaves a <c>jb</c>
    ///     behind holding ReSharper's cache-generation lock.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Unhook before shutting the door, not after: a signal arriving in between would start a pass this
        // method has already decided not to wait for.
        jbRunner.ForegroundRunTimedOut -= OnForegroundRunTimedOut;

        Task? inFlight;
        lock (_gate)
        {
            // Shut the door and read the signal as one step, so no pass can be claimed between the two. A
            // null here means no pass is running (none ever started, or the last one already settled), and
            // there is nothing to drain.
            _stopped = true;
            inFlight = _passInFlight ? _finished.Task : null;
        }

        await _stopping.CancelAsync();

        if (inFlight is null) return;

        try
        {
            // CancellationToken.None on purpose: the host's shutdown token is likely already cancelled,
            // and an orderly drain must not come back looking like a cancellation.
            await inFlight.WaitAsync(DrainTimeout, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("The background cache pre-warm did not stop within {DrainTimeout}", DrainTimeout);
            return;
        }

        // Logged because "drained cleanly" and "abandoned at the timeout" look identical from outside, and only
        // the second explains why the next session found the generation held.
        logger.LogInformation("Drained the background cache pre-warm before shutting down");
    }

    /// <summary>
    ///     Begins a pre-warm pass, and returns immediately.
    /// </summary>
    /// <remarks>
    ///     At most one is ever in flight, and none starts once the host has stopped; beyond that a caller may
    ///     re-arm, which is what lets a call that just hit the run cap be followed by speculative work rather
    ///     than by nothing at all.
    /// </remarks>
    /// <param name="target">
    ///     The solution to warm when the caller already knows it, as a re-arm after a foreground timeout does.
    ///     Omitted, the pass resolves its own target.
    /// </param>
    public void Start(ResolvedConfig? target = null)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            if (_stopped || _passInFlight) return;
            _passInFlight = true;

            if (_finished.Task.IsCompleted)
                _finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            signal = _finished;
        }

        // Task.Run is required here. Config resolution reaches JbLocator, and spawning a process starts it
        // synchronously before the first await, so calling straight through would stall the message pipeline
        // on `jb inspectcode --version`, and holding a message filter open holds the session's loop, and
        // therefore host shutdown, for the length of a jb run.
        _ = Task.Run(() => RunAsync(signal, target));
    }

    /// <summary>
    ///     Arranges a pass for the solution whose foreground run has just hit the cap.
    /// </summary>
    private void OnForegroundRunTimedOut(ResolvedConfig config)
    {
        Start(config);
    }

    /// <summary>Runs one pass and records how it ended.</summary>
    /// <remarks>
    ///     Never throws: it is the top of a fire-and-forget task, so an escape would be an unobserved exception.
    ///     The enabled check and the target resolution sit here rather than in <see cref="WarmAsync" /> for the
    ///     sake of the outcome line: they produce the solution the pass was aimed at, and a pass that ends in a
    ///     cancellation or an unexpected fault has to be able to say which solution that was. It also opens the
    ///     <see cref="RunIdScope" /> that tags every line the pass causes, which tells a pre-warm's lines apart
    ///     from those of a concurrent tool call in the same file.
    /// </remarks>
    private async Task RunAsync(TaskCompletionSource signal, ResolvedConfig? target)
    {
        using IDisposable? runScope = RunIdScope.Begin(logger);
        var elapsed = Stopwatch.StartNew();

        var outcome = WarmUpOutcome.NotRun;
        ResolvedConfig? config = target;
        try
        {
            if (!EnvironmentSwitch.IsOn(environment.GetVariable(EnableVariable)))
            {
                outcome = WarmUpOutcome.Disabled;
            }
            else
            {
                config ??= await TryResolveTargetAsync();
                outcome = config is null ? WarmUpOutcome.NoTarget : await WarmAsync(config);
            }
        }
        catch (OperationCanceledException)
        {
            outcome = WarmUpOutcome.Cancelled;
        }
        catch (Exception exception)
        {
            // A warning, unlike every outcome: everything a pre-warm can expect to meet is an outcome rather
            // than a fault, and Warning is kept for the unexpected.
            logger.LogWarning(exception, "The background cache pre-warm failed unexpectedly");
            outcome = WarmUpOutcome.Failed;
        }
        finally
        {
            // Outcome before Finished, so a caller that awaits the one can read the other.
            Outcome = outcome;
            ReportOutcome(outcome, config, elapsed.Elapsed);

            // The slot before the signal, so `await Finished; Start();` re-arms rather than meeting a pass
            // that has settled but not yet stood down.
            lock (_gate)
            {
                _passInFlight = false;
            }

            // The signal this pass was handed, never the field: a later pass must not be able to strand a
            // caller awaiting an earlier one.
            signal.TrySetResult();
        }
    }

    /// <summary>
    ///     Logs how a pass settled, at <c>Information</c> to pair with the start.
    /// </summary>
    /// <remarks>
    ///     Without an ending line, a log shows pre-warms beginning and never ending, and whether two overlapping
    ///     passes contended, and which of them won, is unanswerable. <see cref="WarmUpOutcome.Disabled" /> is the
    ///     one exception, and it goes to <c>Debug</c> because the startup line already names the switch's
    ///     position, and restating a startup fact once per session is the noise <c>Information</c> is kept
    ///     clear of.
    /// </remarks>
    private void ReportOutcome(WarmUpOutcome outcome, ResolvedConfig? config, TimeSpan elapsed)
    {
        const string template = "Background cache pre-warm finished: {Outcome} for {SolutionPath} after {ElapsedMs} ms";
        string solutionPath = config?.SolutionPath ?? "no target";
        var elapsedMs = (long)elapsed.TotalMilliseconds;

        if (outcome == WarmUpOutcome.Disabled)
            logger.LogDebug(template, outcome, solutionPath, elapsedMs);
        else
            logger.LogInformation(template, outcome, solutionPath, elapsedMs);
    }

    private async Task<WarmUpOutcome> WarmAsync(ResolvedConfig config)
    {
        // The debounce governs every pass, re-arms included. It cannot be hoisted above the resolution in the
        // caller, because it is keyed on what that resolution produces.
        if (WarmedRecentlyByThisBuild(config)) return WarmUpOutcome.AlreadyWarm;

        logger.LogInformation("Pre-warming the ReSharper cache for {SolutionPath}", config.SolutionPath);

        SpeculativeRunOutcome run = await inspectService.WarmCacheAsync(config, _stopping.Token);

        // One arm per ending, because the outcome line this produces sits directly under the run lines
        // JbRunner wrote, and a summary that collapses them contradicts what they say: a pass killed at the
        // cap logs that the cap killed it, and must not call itself a skip in the next line. The default
        // throws for the same reason: a new ending absorbed into "skipped" would misreport spent work as
        // costless, silently.
        return run switch
        {
            SpeculativeRunOutcome.NotStarted => WarmUpOutcome.Skipped,
            SpeculativeRunOutcome.Completed => WarmUpOutcome.Warmed,
            SpeculativeRunOutcome.Failed => WarmUpOutcome.Failed,
            SpeculativeRunOutcome.Capped => WarmUpOutcome.Capped,
            SpeculativeRunOutcome.StoodDown => WarmUpOutcome.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(run), run, "Unmapped speculative run outcome.")
        };
    }

    /// <summary>
    ///     Whether a run against this cache generation succeeded within <see cref="RecentlyWarmWindow" />, by
    ///     the <c>jb</c> build this pass would start.
    /// </summary>
    /// <remarks>
    ///     The build half is <see cref="JbWarmMarker.WrittenByAnotherBuild" />'s judgement, the same one the
    ///     cache-state line makes. A fresh marker that another build stamped vouches for a cache this <c>jb</c>
    ///     rebuilds in place, so skipping would hand that rebuild to the first call. A marker whose content
    ///     cannot be read names no build, so it costs a pass rather than saving one.
    /// </remarks>
    private bool WarmedRecentlyByThisBuild(ResolvedConfig config)
    {
        if (!JbWarmMarker.IsFreshWithin(config.SolutionPath, config.CacheHome, RecentlyWarmWindow, logger)) return false;

        string markerPath = JbWarmMarker.PathFor(config.SolutionPath, config.CacheHome);
        string? markerJbVersion = JbWarmMarker.TryReadJbVersion(markerPath, logger);

        return !JbWarmMarker.WrittenByAnotherBuild(markerJbVersion, config.JbVersion);
    }

    /// <summary>
    ///     Resolves the solution a pass with no given target warms.
    /// </summary>
    /// <remarks>
    ///     None means no <c>jb</c> installed, or no solution in the working directory, which is the ordinary
    ///     shape of a server started somewhere that is not a .NET repo. There is nothing to warm and nobody to
    ///     tell.
    /// </remarks>
    private async Task<ResolvedConfig?> TryResolveTargetAsync()
    {
        try
        {
            return await configResolver.ResolveAsync(null, _stopping.Token);
        }
        catch (UserErrorException exception)
        {
            logger.LogDebug(exception, "Nothing to pre-warm");
            return null;
        }
    }
}