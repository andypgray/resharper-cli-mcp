using Serilog;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Services;

/// <summary>
///     The one path by which a <c>jb</c> subcommand is run: it takes the cross-process
///     <see cref="JbRunLock" /> for the solution's cache generation, spawns <c>jb</c> under the run
///     timeout, and turns a non-zero exit into a <see cref="UserErrorException" /> quoting the tail of
///     standard error. Inspect and cleanup share one cache generation, so the lock has to be taken in one
///     place rather than at both call sites by convention.
/// </summary>
/// <remarks>
///     <para>
///         Queue time is deliberately outside the run budget: the timeout below is armed inside
///         <see cref="ProcessRunner" />, which starts only once the lock is held, so a call that waited for
///         another run still gets its own full budget.
///     </para>
///     <para>
///         Two entry points, one spawn. <see cref="RunAsync" /> serves a call the user made: it queues for
///         the lock and throws on failure. <see cref="TryRunAsync" /> serves speculative work — today only
///         <see cref="CacheWarmer" /> — and does the opposite at every turn: it skips rather than queues, and
///         reports rather than throws. Both go through <see cref="SpawnAsync" />, which keeps this class the
///         sole place a <c>jb</c> process starts.
///     </para>
/// </remarks>
internal sealed class JbRunner(IProcessRunner processRunner, JbRunLock runLock)
{
    private const int StandardErrorTailLength = 2000;

    /// <summary>Wall-clock cap on one <c>jb</c> run, after which its process tree is killed.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     The speculative run in flight, or <see langword="null" />. Published so a real call can reclaim
    ///     the cache generation instead of queueing behind work nobody asked for.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The published source is never disposed. A foreground caller that has already taken the
    ///         reference may be about to <c>Cancel()</c> it, and that window cannot be closed without holding
    ///         a lock across a cancellation whose callbacks tree-kill a process inline. One undisposed linked
    ///         source — bounded at one per process, because the pre-warm starts at most once — is the cheaper
    ///         trade, and <see cref="CancelBackgroundRun" /> catches the disposal race regardless.
    ///     </para>
    ///     <para>
    ///         Cancelling is not instantaneous. The lease drops only after <see cref="ProcessRunner" /> sees
    ///         the cancellation, tree-kills <c>jb</c>, and reaps it, so a foreground call can still spend
    ///         milliseconds to a few seconds on the lock after cancelling. Bounded and far better than
    ///         waiting out the run, but yielding is not free.
    ///     </para>
    ///     <para>
    ///         In-process only. A pre-warm running in another server process cannot be yielded to, and a call
    ///         there queues behind it exactly as it queues behind another session's real call.
    ///     </para>
    /// </remarks>
    private CancellationTokenSource? _backgroundRun;

    /// <summary>
    ///     Set once a call the user made has reached this runner, and never cleared. A real run analyses the
    ///     whole solution into the same cache generation a pre-warm would, so once one has arrived the
    ///     speculative run has nothing left to buy — and starting one anyway is the only way pre-warming
    ///     could ever delay a call inside this process. Reading it <em>after</em> publishing
    ///     <see cref="_backgroundRun" /> is what closes the gap between the two: whichever of the pair reads
    ///     stale, the other has already seen the write it needed.
    /// </summary>
    private int _foregroundArrived;

    /// <summary>
    ///     Run <c>jb</c> with <paramref name="arguments" /> — whose first entry is the subcommand — against
    ///     the solution in <paramref name="config" />, returning its result for the caller's own
    ///     post-checks.
    /// </summary>
    public async Task<ProcessResult> RunAsync(
        ResolvedConfig config,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        // Before queueing, not after: a call arriving ten seconds into a cold pre-warm would otherwise pay
        // the whole queue wait and then its own full run, which is strictly worse than never pre-warming.
        CancelBackgroundRun();

        using IDisposable runLease = await runLock.AcquireAsync(config.SolutionPath, config.CacheHome, cancellationToken);

        ProcessResult result = await SpawnAsync(config, arguments, cancellationToken);

        if (result.ExitCode != 0)
            throw new UserErrorException(
                $"jb {arguments[0]} exited with code {result.ExitCode}.\n{StandardErrorTail(result.StandardError)}");

        return result;
    }

    /// <summary>
    ///     Run <c>jb</c> speculatively: only if no real call has arrived in this process, only if the cache
    ///     generation is free right now, and only until a real call wants it. Returns <see langword="null" />
    ///     when the run did not happen or was given up — neither is an error — and otherwise the result,
    ///     non-zero exit codes included, because background work has no channel to report a failure through
    ///     and its caller decides what a failure means.
    /// </summary>
    public async Task<ProcessResult?> TryRunAsync(
        ResolvedConfig config,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        // Publish before taking the lease, then re-read the flag: a foreground call that has already gone
        // past its own cancel point would find nothing to cancel, and would then queue behind a run started
        // a moment later. Publish-then-check is what makes "a real call is never delayed by a pre-warm in
        // this process" a rule rather than a near-certainty.
        var mine = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Interlocked.Exchange(ref _backgroundRun, mine);

        try
        {
            if (Volatile.Read(ref _foregroundArrived) != 0) return null;

            using IDisposable? runLease = runLock.TryAcquire(config.SolutionPath, config.CacheHome);
            if (runLease is null) return null;

            return await SpawnAsync(config, arguments, mine.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A foreground run took over. ProcessRunner surfaces that and our own caller's cancellation
            // identically — an OCE on the token it was handed — so this filter is the only discriminator.
            return null;
        }
        finally
        {
            // Compare-and-swap, not a blind clear: by now the field may already hold a *later* run, and a
            // finished pre-warm must not have its successor cancelled on its behalf.
            Interlocked.CompareExchange(ref _backgroundRun, null, mine);
        }
    }

    /// <summary>
    ///     The single <c>jb</c> spawn. A clean exit stamps the warm marker, so the pre-warm debounce records
    ///     every successful run — foreground tool calls included, and <c>cleanupcode</c> as much as
    ///     <c>inspectcode</c>, since both analyse the whole solution into the same cache generation — rather
    ///     than relying on one call site remembering to.
    /// </summary>
    private async Task<ProcessResult> SpawnAsync(
        ResolvedConfig config,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunAsync(
            config.JbExecutablePath, arguments, Timeout, cancellationToken);

        if (result.ExitCode == 0) JbWarmMarker.Stamp(config.SolutionPath, config.CacheHome);

        return result;
    }

    /// <summary>
    ///     Give a real call the cache generation back: retire speculative work for the life of the process,
    ///     then cancel the run holding the lease, if any. A failure here degrades to today's behaviour — the
    ///     call queues — because a background optimisation must never be able to fail one.
    /// </summary>
    private void CancelBackgroundRun()
    {
        // Ordered before the exchange below, and paired with the read in TryRunAsync: between them, a pre-warm
        // either sees this flag or has already published a source for this call to cancel.
        Interlocked.Exchange(ref _foregroundArrived, 1);

        try
        {
            Interlocked.Exchange(ref _backgroundRun, null)?.Cancel();
        }
        catch (Exception exception) when (exception is AggregateException or ObjectDisposedException)
        {
            Log.Warning(exception, "Could not cancel the background cache pre-warm; this call will queue behind it instead");
        }
    }

    /// <summary>
    ///     The last <see cref="StandardErrorTailLength" /> characters of <paramref name="standardError" />,
    ///     trailing whitespace trimmed — enough of a failed run's output to diagnose it without flooding
    ///     the response.
    /// </summary>
    internal static string StandardErrorTail(string standardError)
    {
        string trimmed = standardError.TrimEnd();
        return trimmed.Length <= StandardErrorTailLength ? trimmed : trimmed[^StandardErrorTailLength..];
    }
}