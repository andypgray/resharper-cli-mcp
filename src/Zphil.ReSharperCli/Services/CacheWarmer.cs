using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Services;

/// <summary>
///     The server's one piece of background work: once per session, as soon as a client connects, populate
///     the ReSharper cache generation the first tool call is going to want. A session usually idles for
///     minutes between the handshake and its first call, and a cold <c>jb inspectcode</c> costs minutes, so
///     this spends the idle window instead of the user's. <see cref="Pipeline.PreWarmTrigger" /> owns the
///     signal that gets it going.
/// </summary>
/// <remarks>
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

    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stopping = new();

    private int _started;

    /// <summary>How the pre-warm ended; <see cref="WarmUpOutcome.NotRun" /> until it has.</summary>
    internal WarmUpOutcome Outcome { get; private set; } = WarmUpOutcome.NotRun;

    /// <summary>Completes once the pre-warm has settled, on every path including the ones that never ran <c>jb</c>.</summary>
    internal Task Finished => _finished.Task;

    public void Dispose()
    {
        _stopping.Dispose();
    }

    /// <summary>
    ///     Deliberately a no-op: the host starts before any client has connected, so there is nothing to warm
    ///     for yet. <see cref="Start" /> is what actually begins the work.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Cancel a pre-warm in flight and wait for it to let go, so the process never leaves a <c>jb</c>
    ///     behind holding ReSharper's cache-generation lock.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Claim the start slot on the way out, so a message arriving now cannot start a run this method has
        // already decided not to wait for. A false here means nothing ever started.
        bool wasStarted = Interlocked.Exchange(ref _started, 1) != 0;

        await _stopping.CancelAsync();

        if (!wasStarted) return;

        try
        {
            // CancellationToken.None on purpose: the host's shutdown token is very likely already cancelled,
            // and an orderly drain must not come back looking like a cancellation.
            await Finished.WaitAsync(DrainTimeout, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("The background cache pre-warm did not stop within {DrainTimeout}", DrainTimeout);
        }
    }

    /// <summary>
    ///     Begin the pre-warm, at most once per process. Returns immediately: the caller sits in the server's
    ///     incoming-message pipeline, which every later message queues behind.
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        // Task.Run is load-bearing rather than stylistic. Config resolution reaches JbLocator, and spawning a
        // process starts it synchronously before the first await, so calling straight through would stall the
        // message pipeline on `jb inspectcode --version` — and holding a message filter open holds the
        // session's loop, and therefore host shutdown, for the length of a jb run.
        _ = Task.Run(RunAsync);
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
    private async Task RunAsync()
    {
        var outcome = WarmUpOutcome.NotRun;
        try
        {
            outcome = await WarmAsync();
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
            _finished.TrySetResult();
        }
    }

    private async Task<WarmUpOutcome> WarmAsync()
    {
        if (!IsEnabled(environment.GetVariable(EnableVariable))) return WarmUpOutcome.Disabled;

        ResolvedConfig config;
        try
        {
            config = await configResolver.ResolveAsync(null, _stopping.Token);
        }
        catch (UserErrorException exception)
        {
            // No jb installed, or no solution in the working directory: the ordinary shape of a server
            // started somewhere that is not a .NET repo. There is nothing to warm and nobody to tell.
            logger.LogDebug(exception, "Nothing to pre-warm");
            return WarmUpOutcome.NoTarget;
        }

        if (JbWarmMarker.IsFreshWithin(config.SolutionPath, config.CacheHome, RecentlyWarmWindow))
            return WarmUpOutcome.AlreadyWarm;

        logger.LogInformation("Pre-warming the ReSharper cache for {SolutionPath}", config.SolutionPath);

        var result = await inspectService.WarmCacheAsync(config, _stopping.Token);

        if (result is null) return WarmUpOutcome.Skipped;

        return result.Value.ExitCode == 0 ? WarmUpOutcome.Warmed : WarmUpOutcome.Failed;
    }
}