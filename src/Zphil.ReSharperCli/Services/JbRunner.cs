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
///         Queue time is deliberately outside the run budget: <paramref name="runTimeout" /> is armed inside
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
///     <para>
///         Both also give <see cref="CacheTransplanter" /> its one chance to seed the cache, in the window
///         between holding the generation's lease and starting <c>jb</c> — the only moment at which the
///         directory is known to be nobody else's and not yet open. It is placed here rather than at the two
///         service call sites for the same reason the lock is: inspect and cleanup share one cache
///         generation, and a rule kept by convention at two call sites is a rule until someone adds a third.
///     </para>
/// </remarks>
/// <param name="runTimeout">
///     Wall-clock cap on one <c>jb</c> run, after which its process tree is killed. Resolved from
///     <see cref="JbRunTimeout" /> at the composition root, which hands the same value to
///     <see cref="JbRunLock" /> so wait and run stay one number.
/// </param>
internal sealed class JbRunner(
    IProcessRunner processRunner,
    JbRunLock runLock,
    CacheTransplanter transplanter,
    TimeSpan runTimeout)
{
    private const int StandardErrorTailLength = 2000;

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

        await transplanter.TryTransplantAsync(config, cancellationToken);

        ProcessResult result;
        try
        {
            result = await SpawnAsync(config, arguments, cancellationToken);
        }
        catch (ProcessTimeoutException exception)
        {
            throw new UserErrorException(TimedOutMessage(arguments[0]), exception);
        }

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

            await transplanter.TryTransplantAsync(config, mine.Token);

            return await SpawnAsync(config, arguments, mine.Token);
        }
        catch (ProcessTimeoutException)
        {
            // The cap is a foreground protection: it is there so a call the user is waiting on cannot hang
            // on a stuck jb. Nobody is waiting on this one, and a cold solution big enough to exceed the cap
            // is precisely the solution pre-warming exists for — so running out of budget here is an
            // ordinary skip, not the "unexpected failure" the warmer would otherwise log a warning for.
            return null;
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
    ///     than relying on one call site remembering to. The same exit discharges any cold tombstone a reset
    ///     left: the cache this run rebuilt is the solution's own, so there is no longer a reset to protect.
    /// </summary>
    private async Task<ProcessResult> SpawnAsync(
        ResolvedConfig config,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunAsync(
            config.JbExecutablePath, arguments, runTimeout, cancellationToken);

        if (result.ExitCode != 0) return result;

        JbWarmMarker.Stamp(config.SolutionPath, config.CacheHome);
        JbColdTombstone.Clear(config.SolutionPath, config.CacheHome);

        return result;
    }

    /// <summary>
    ///     What a foreground caller is told when its run hit the cap. Three things it cannot work out for
    ///     itself, and the reason this message is not left to <see cref="ProcessRunner" />: the cap belongs
    ///     to this server rather than to <c>jb</c> or to the MCP client, there is an environment variable
    ///     that moves it, and the obvious next move does not work — scoping with <c>files</c> narrows what
    ///     <c>jb</c> reports and never what it analyses, so a retry scoped to one file is just as slow.
    /// </summary>
    private string TimedOutMessage(string subcommand)
    {
        return $"jb {subcommand} timed out after {ProcessRunner.FormatDuration(runTimeout)} and was stopped.\n"
               + $"That cap is this server's, not jb's own: raise it by setting {JbRunTimeout.Variable} (in seconds) "
               + "in this server's env block in your MCP client config, then restart the server.\n"
               + "A run that long is almost always a cold ReSharper cache. Scoping the next call with `files` will "
               + "not help — jb analyses the whole solution whatever the report is narrowed to — but the cache keeps "
               + "what this run built, so a retry resumes from there.";
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
    ///     Append the config-derived options every <c>jb</c> subcommand takes — <c>--caches-home</c>,
    ///     <c>--settings</c>, <c>-x</c>, <c>--source</c> — in one place. Inspect and cleanup must pass
    ///     identical configuration to open the same cache generation, so a new axis added here reaches
    ///     both builders at once instead of landing in one and silently missing the other. The optional
    ///     values are null-or-meaningful by <c>ConfigResolver</c>'s contract, so presence is a null check.
    /// </summary>
    internal static void AppendConfigArguments(List<string> arguments, ResolvedConfig config)
    {
        arguments.Add($"--caches-home={config.CacheHome}");

        if (config.SettingsPath is not null) arguments.Add($"--settings={config.SettingsPath}");

        if (config.Extensions is not null) arguments.Add($"-x={config.Extensions}");

        if (config.ExtensionSource is not null) arguments.Add($"--source={config.ExtensionSource}");
    }

    /// <summary>The <c>--include</c> flag: jb takes one argument joining the patterns with <c>;</c>.</summary>
    internal static string IncludeArgument(IReadOnlyList<string> files)
    {
        return $"--include={string.Join(";", files)}";
    }

    /// <summary>
    ///     The last <see cref="StandardErrorTailLength" /> characters of <paramref name="standardError" />,
    ///     trailing whitespace trimmed — enough of a failed run's output to diagnose it without flooding
    ///     the response. A null tolerated for the same reason <see cref="JbLocator" /> tolerates a null
    ///     standard output: a defaulted <see cref="ProcessResult" /> carries one, and the paths that quote a
    ///     tail exist to report a failure, not to add one.
    /// </summary>
    internal static string StandardErrorTail(string? standardError)
    {
        string trimmed = standardError?.TrimEnd() ?? string.Empty;
        return trimmed.Length <= StandardErrorTailLength ? trimmed : trimmed[^StandardErrorTailLength..];
    }
}