using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Services;

/// <summary>
///     The server's one piece of background work: populate the ReSharper cache generation a tool call is
///     going to want, in an idle window rather than in the user's. A session usually idles for minutes
///     between the handshake and its first call, and a cold <c>jb inspectcode</c> costs minutes.
///     <see cref="Pipeline.PreWarmTrigger" /> owns the signal that gets the first pass going.
/// </summary>
/// <remarks>
///     <para>
///         One <em>kind</em> of speculative work and at most one pass in flight, but not at most one pass per
///         process. A pass may recur on a signal that a real call has just handed the cache generation back —
///         today only a foreground run hitting the cap — and never on a timer or per message. A timer would
///         be a second background job; a pass that started itself would be a loop; a per-message trigger
///         would pay a settings parse and a <c>jb</c> re-probe on every message for nothing.
///     </para>
///     <para>
///         It decides <em>when</em> a warm-up runs; <see cref="InspectService.WarmCacheAsync" /> decides
///         <em>how</em>, so no jb argument is ever built here. The target is whatever
///         <c>ConfigResolver.ResolveAsync(null, …)</c> resolves — precisely the solution a call with no
///         <c>solutionPath</c> would use — rather than a fourth discovery axis competing with the documented
///         <c>JB_SOLUTION_PATH</c> → working-directory precedence.
///     </para>
///     <para>
///         Speculative work must never be able to fail, delay, or report through a tool call, so every path
///         here ends in a <see cref="WarmUpOutcome" /> and nothing propagates: disabled, no target, already
///         warm, and someone-else-is-running are all ordinary outcomes that stay out of the log, and only a
///         genuinely unexpected exception is a warning.
///     </para>
///     <para>
///         It is an <see cref="IHostedService" /> solely for <see cref="StopAsync" />. Cancelling alone would
///         kill <c>jb</c> without <em>waiting</em> for it, and a <c>jb</c> outliving this process keeps
///         ReSharper's own cache-generation lock after the OS has dropped our lock-file handle — the one
///         orphan the run lock cannot protect the next session from.
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
    ///     it. Errs long on purpose: a skipped pre-warm costs nothing beyond what today already costs, while a
    ///     needless one costs a couple of minutes of multi-core CPU. Without it, a user-scope server would
    ///     analyse a solution at every session start in every C# repo.
    /// </summary>
    internal static readonly TimeSpan RecentlyWarmWindow = TimeSpan.FromHours(1);

    /// <summary>
    ///     Long enough for a killed <c>jb</c> tree to be reaped (<see cref="ProcessRunner" /> allows five seconds), and
    ///     no longer.
    /// </summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private static readonly string[] OffSpellings = ["off", "false", "0", "no", "disabled"];

    /// <summary>
    ///     Guards the three fields below as one. Not decoration: <see cref="Start" /> claims the slot and
    ///     republishes the completion signal in a single step, and <see cref="StopAsync" /> closes the door
    ///     and reads that signal in a single step. Interleaved without it, <c>Start</c> claims the slot,
    ///     <c>StopAsync</c> reads the <em>previous</em> pass's already-settled task, drains instantly and
    ///     returns, and <c>Start</c> then spawns a <c>jb</c> after shutdown.
    /// </summary>
    private readonly Lock _gate = new();

    private readonly CancellationTokenSource _stopping = new();

    private TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _passInFlight;
    private bool _stopped;

    /// <summary>
    ///     How the last settled pass ended; <see cref="WarmUpOutcome.NotRun" /> until one has. Never reset
    ///     when a pass begins, so it always names a real outcome rather than briefly reading as "not run".
    /// </summary>
    internal WarmUpOutcome Outcome { get; private set; } = WarmUpOutcome.NotRun;

    /// <summary>
    ///     Completes once the pass in flight has settled, on every path including the ones that never ran
    ///     <c>jb</c>. Republished when a pass starts, so a caller awaiting it after a re-arm waits for that
    ///     pass rather than sailing past on the previous one's completed task.
    /// </summary>
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
    ///     Starts nothing — the host starts before any client has connected, so there is nothing to warm for
    ///     yet, and <see cref="Start" /> is still what begins a pass. What it does is subscribe, so that a
    ///     foreground run hitting the run cap arranges one.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        jbRunner.ForegroundRunTimedOut += OnForegroundRunTimedOut;
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Cancel a pre-warm in flight and wait for it to let go, so the process never leaves a <c>jb</c>
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
            // null here means no pass is running — either none ever started, or the last one already settled
            // — and there is nothing to drain.
            _stopped = true;
            inFlight = _passInFlight ? _finished.Task : null;
        }

        await _stopping.CancelAsync();

        if (inFlight is null) return;

        try
        {
            // CancellationToken.None on purpose: the host's shutdown token is very likely already cancelled,
            // and an orderly drain must not come back looking like a cancellation.
            await inFlight.WaitAsync(DrainTimeout, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("The background cache pre-warm did not stop within {DrainTimeout}", DrainTimeout);
        }
    }

    /// <summary>
    ///     Begin a pre-warm pass. At most one is ever in flight, and none starts once the host has stopped;
    ///     beyond that a caller may re-arm, which is what lets a call that just hit the run cap be followed by
    ///     speculative work rather than by nothing at all. Returns immediately.
    /// </summary>
    /// <param name="target">
    ///     The solution to warm, when the caller already knows it — a re-arm after a foreground timeout does,
    ///     and it is the solution that actually timed out rather than whatever the working directory would
    ///     resolve to. Omitted, the pass resolves its own target as it always has.
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

        // Task.Run is load-bearing rather than stylistic. Config resolution reaches JbLocator, and spawning a
        // process starts it synchronously before the first await, so calling straight through would stall the
        // message pipeline on `jb inspectcode --version` — and holding a message filter open holds the
        // session's loop, and therefore host shutdown, for the length of a jb run.
        _ = Task.Run(() => RunAsync(signal, target));
    }

    /// <summary>
    ///     A call the user made ran out of budget. That is the best moment speculative work ever gets: the
    ///     cache is part-built, the error just told the user a retry resumes from there, and the user is now
    ///     idle reading it. One pass per timeout, and no pass re-arms itself, so recurrence is unbounded but
    ///     paced entirely by the user making another call.
    /// </summary>
    private void OnForegroundRunTimedOut(ResolvedConfig config)
    {
        Start(config);
    }

    /// <summary>
    ///     Whether the pre-warm is on. <c>off</c>, <c>false</c>, <c>0</c>, <c>no</c> and <c>disabled</c> turn
    ///     it off; everything else — including unset and unrecognised — leaves the shipped default in place,
    ///     matching how <see cref="Infrastructure.SerilogConfiguration.ParseLogLevel" /> reads its variable.
    /// </summary>
    internal static bool IsEnabled(string? envValue)
    {
        return !OffSpellings.Contains(envValue?.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Never throws: it is the top of a fire-and-forget task, so an escape would be an unobserved exception.</summary>
    private async Task RunAsync(TaskCompletionSource signal, ResolvedConfig? target)
    {
        var outcome = WarmUpOutcome.NotRun;
        try
        {
            outcome = await WarmAsync(target);
        }
        catch (OperationCanceledException)
        {
            outcome = WarmUpOutcome.Cancelled;
        }
        catch (Exception exception)
        {
            // The only warning this class can write. Everything a pre-warm can expect to meet is an outcome,
            // not a fault, and the log promises to record unexpected failures only.
            logger.LogWarning(exception, "The background cache pre-warm failed unexpectedly");
            outcome = WarmUpOutcome.Failed;
        }
        finally
        {
            // Outcome before Finished, so a caller that awaits the one can read the other.
            Outcome = outcome;
            logger.LogDebug("Background cache pre-warm finished: {Outcome}", outcome);

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

    private async Task<WarmUpOutcome> WarmAsync(ResolvedConfig? target)
    {
        if (!IsEnabled(environment.GetVariable(EnableVariable))) return WarmUpOutcome.Disabled;

        ResolvedConfig? config = target ?? await TryResolveTargetAsync();
        if (config is null) return WarmUpOutcome.NoTarget;

        // The debounce governs every pass, re-arms included. It cannot be hoisted above the resolution above
        // it, because it is keyed on what that resolution produces.
        if (JbWarmMarker.IsFreshWithin(config.SolutionPath, config.CacheHome, RecentlyWarmWindow))
            return WarmUpOutcome.AlreadyWarm;

        logger.LogInformation("Pre-warming the ReSharper cache for {SolutionPath}", config.SolutionPath);

        var result = await inspectService.WarmCacheAsync(config, _stopping.Token);

        if (result is null) return WarmUpOutcome.Skipped;

        return result.Value.ExitCode == 0 ? WarmUpOutcome.Warmed : WarmUpOutcome.Failed;
    }

    /// <summary>
    ///     The solution a pass with no given target warms, or <see langword="null" /> when there is none —
    ///     no <c>jb</c> installed, or no solution in the working directory, which is the ordinary shape of a
    ///     server started somewhere that is not a .NET repo. There is nothing to warm and nobody to tell.
    /// </summary>
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