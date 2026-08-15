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
///         sole place a <c>jb</c> process starts. Which of the two wins when they collide is
///         <see cref="JbRunYield" />'s to say, not this class's: the rule belongs to every caller the user is
///         waiting on, and a cache reset is one that runs no <c>jb</c> at all.
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
    JbRunYield runYield,
    CacheTransplanter transplanter,
    TimeSpan runTimeout)
{
    private const int StandardErrorTailLength = 2000;

    /// <summary>
    ///     Raised when a call the user made hit the run cap, carrying the configuration that run used. A
    ///     part-built cache and an idle user is the best moment speculative work ever gets, and nothing else
    ///     in the process can see that moment.
    /// </summary>
    /// <remarks>
    ///     Deliberately never raised from <see cref="TryRunAsync" />. A speculative run that re-armed on its
    ///     own timeout would warm, hit the cap, re-arm, and repeat for the life of the process; raised only
    ///     from the foreground path, recurrence advances solely when the user makes another call. The
    ///     <see cref="ResolvedConfig" /> travels with it so a listener warms the solution that actually
    ///     failed — not whatever the server's working directory resolves to, which is a different solution
    ///     whenever a client points the server at a worktree — and pays no resolution of its own.
    /// </remarks>
    internal event Action<ResolvedConfig>? ForegroundRunTimedOut;

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
        var timedOut = false;
        try
        {
            // Entered before queueing, not after: a call arriving ten seconds into a cold pre-warm would
            // otherwise pay the whole queue wait and then its own full run, which is strictly worse than
            // never pre-warming.
            using IDisposable foreground = runYield.EnterForeground();

            // Both scoped inside this try on purpose, so the lease is released and the claim stood down
            // before the finally below announces a timeout. Announce while still holding either and the
            // re-armed pass would settle as a skip — the re-arm would buy nothing, silently. Disposal is the
            // reverse of declaration, so the lease goes first and the count outlives it by a hair.
            using IDisposable runLease = await runLock.AcquireAsync(config.SolutionPath, config.CacheHome, cancellationToken);

            await transplanter.TryTransplantAsync(config, cancellationToken);

            ProcessResult result;
            try
            {
                result = await SpawnAsync(config, arguments, cancellationToken);
            }
            catch (ProcessTimeoutException exception)
            {
                timedOut = true;
                throw new UserErrorException(TimedOutMessage(arguments[0]), exception);
            }

            if (result.ExitCode != 0)
                throw new UserErrorException(
                    $"jb {arguments[0]} exited with code {result.ExitCode}.\n{StandardErrorTail(result.StandardError)}");

            return result;
        }
        finally
        {
            if (timedOut) AnnounceTimeout(config);
        }
    }

    /// <summary>
    ///     Run <c>jb</c> speculatively: only if no real call is in flight in this process, only if the cache
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
        // Claimed before the lease is taken: the claim is what a caller the user is waiting on cancels, and
        // a null answer means one is already in flight.
        using JbRunYield.SpeculativeRun? speculative = runYield.TryEnterSpeculative(cancellationToken);
        if (speculative is null) return null;

        try
        {
            using IDisposable? runLease = runLock.TryAcquire(config.SolutionPath, config.CacheHome);
            if (runLease is null) return null;

            await transplanter.TryTransplantAsync(config, speculative.Token);

            // ProcessRunner calls process.Start() before it ever looks at its token, so a pre-warm cancelled
            // in this window would fork a jb only to tree-kill and reap it — holding, for those seconds, the
            // very lease the real call is queueing for. Cheap to check, and re-arming makes it reachable more
            // than once per process.
            speculative.Token.ThrowIfCancellationRequested();

            return await SpawnAsync(config, arguments, speculative.Token);
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
            // A caller the user is waiting on took over. ProcessRunner surfaces that and our own caller's
            // cancellation identically — an OCE on the token it was handed — so this filter is the only
            // discriminator.
            return null;
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
               + "most of what this run built, so a retry resumes rather than starting over.";
    }

    /// <summary>
    ///     Tell whoever is listening that a foreground run ran out of budget. Swallows anything a subscriber
    ///     throws: this fires while the <see cref="UserErrorException" /> carrying
    ///     <see cref="TimedOutMessage" /> is unwinding, and a throwing listener would replace the one message
    ///     that tells the user whose cap it was with an unrelated failure. The same bargain
    ///     <see cref="JbRunYield" /> makes on the way in — an optimisation may not fail a call.
    /// </summary>
    private void AnnounceTimeout(ResolvedConfig config)
    {
        try
        {
            ForegroundRunTimedOut?.Invoke(config);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "A listener for the run-timed-out signal threw; the timeout itself is still reported to the caller");
        }
    }

    /// <summary>
    ///     Append the config-derived options every <c>jb</c> subcommand takes — <c>--caches-home</c>,
    ///     <c>--settings</c>, <c>-x</c>, <c>--source</c> — in one place. Inspect and cleanup must pass
    ///     identical configuration to open the same cache generation, so a new axis added here reaches
    ///     both builders at once instead of landing in one and silently missing the other. The optional
    ///     values are null-or-meaningful by <c>ConfigResolver</c>'s contract, so presence is a null check —
    ///     except <c>--settings</c>, which is present only for a file <c>jb</c> cannot discover itself:
    ///     it mounts a Custom layer above the whole stack, so passing a discovered file would demote every
    ///     project's own <c>.csproj.DotSettings</c> rather than change nothing.
    /// </summary>
    internal static void AppendConfigArguments(List<string> arguments, ResolvedConfig config)
    {
        arguments.Add($"--caches-home={config.CacheHome}");

        if (config.SettingsPathIsCustomLayer) arguments.Add($"--settings={config.SettingsPath}");

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