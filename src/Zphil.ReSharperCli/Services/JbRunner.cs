using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Formatting;

namespace Zphil.ReSharperCli.Services;

/// <summary>
///     Represents a <c>jb</c> run that exited non-zero, carrying the code it exited with.
/// </summary>
/// <remarks>
///     A distinct type so a caller can recognise this failure and restate it with knowledge
///     <see cref="JbRunner" /> does not have. Catch this type rather than <see cref="UserErrorException" />:
///     the timeout path throws the base type too, and its message must reach the caller unchanged, because a
///     cleanup killed at the cap may have rewritten files and cannot claim otherwise.
/// </remarks>
internal sealed class JbExitCodeException(string message, int exitCode, string standardErrorTail)
    : UserErrorException(message)
{
    /// <summary>Gets the code <c>jb</c> exited with, which is never zero.</summary>
    public int ExitCode { get; } = exitCode;

    /// <summary>
    ///     Gets the tail of the run's standard error, already bounded by
    ///     <see cref="JbRunner.StandardErrorTail" />, so a caller restating the failure quotes what this
    ///     message quotes rather than re-trimming it.
    /// </summary>
    public string StandardErrorTail { get; } = standardErrorTail;
}

/// <summary>
///     Runs every <c>jb</c> subcommand this server starts: it takes the run slot and the cross-process
///     <see cref="JbRunLock" /> for the solution's cache generation, spawns <c>jb</c> under the run timeout,
///     and turns a non-zero exit into a <see cref="JbExitCodeException" /> quoting the tail of standard error.
/// </summary>
/// <remarks>
///     <para>
///         Every subcommand opens the same cache generation, so the slot, the lock and the cache seeding are
///         taken on this spawn path rather than by each service. <see cref="SpawnAsync" /> is the only place a
///         <c>jb</c> subcommand starts, and a new entry point has to reach it rather than spawn its own.
///     </para>
///     <para>
///         <paramref name="runTimeout" /> caps one run's wall clock, after which its process tree is killed,
///         and the same value bounds <see cref="JbRunLock" />'s wait. Queue time is outside it: the cap is armed
///         inside <see cref="ProcessRunner" />, which starts only once the lock is held, so a call that waited
///         for another run still gets its own full budget.
///     </para>
///     <para>
///         <see cref="CacheTransplanter" /> seeds the cache between holding the generation's lease and
///         starting <c>jb</c>, the only moment at which the directory is known to be nobody else's and not yet
///         open.
///     </para>
///     <para>
///         <paramref name="heartbeatInterval" /> sets how often a run in flight reports itself, defaulting to
///         <see cref="JbRunProgress.HeartbeatInterval" />. It is a parameter so an integration test driving the
///         whole pipeline need not wait a production interval for a second beat.
///     </para>
/// </remarks>
internal sealed class JbRunner(
    IProcessRunner processRunner,
    JbRunLock runLock,
    JbRunYield runYield,
    JbRunSlot runSlot,
    CacheTransplanter transplanter,
    TimeSpan runTimeout,
    ILogger<JbRunner> logger,
    TimeSpan? heartbeatInterval = null)
{
    private const int StandardErrorTailLength = 2000;

    /// <summary>How each entry point names itself in a run's opening line.</summary>
    private const string ForACall = "for a call";

    private const string Speculative = "speculative";

    /// <summary>What a <c>cleanupcode</c> killed at the cap cannot rule out, in every cache state.</summary>
    private const string RewrittenWarning = "Files this run reached before it was stopped may already have been rewritten.";

    /// <summary>What a run that loaded the whole solution is told about narrowing <c>files</c>.</summary>
    private const string ScopingCannotHelp =
        "Scoping the next call with `files` will not help — jb analyses the whole solution whatever the report is narrowed to.";

    /// <summary>
    ///     Where a variable this server reads once at startup is set, and what makes a new value take effect.
    /// </summary>
    private const string InEnvBlockThenRestart = "in this server's env block in your MCP client config, then restart the server.";

    /// <summary>
    ///     Whether this server session has already said that <c>jb</c>'s cache-generation naming no longer
    ///     matches what <see cref="JbSolutionCacheHash" /> reproduces.
    /// </summary>
    /// <remarks>
    ///     Drift describes this machine's <c>jb</c>, not one run, so it is said once rather than on every
    ///     successful run. An instance field, which is why the latch lives here rather than beside the static
    ///     marker it describes: one instance serves one server session, so "once" is scoped to the session whose
    ///     log the warning lands in, where a static latch would let the first session in a process hosting
    ///     several absorb the warning for the rest.
    /// </remarks>
    private int _driftWarned;

    /// <summary>
    ///     Raised when a call the user made hits the run cap, carrying the configuration that run used.
    /// </summary>
    /// <remarks>
    ///     That moment, a part-built cache and an idle user, is when speculative work is worth most, and only
    ///     this class sees it. Never raised from <see cref="TryRunAsync" />: a pass re-armed by its own timeout
    ///     would warm, hit the cap and re-arm for the life of the process. The <see cref="ResolvedConfig" />
    ///     travels with it so a listener warms the solution that failed, not whatever the server's working
    ///     directory resolves to (a different solution whenever a client points the server at a worktree), and
    ///     pays no resolution of its own.
    /// </remarks>
    internal event Action<ResolvedConfig>? ForegroundRunTimedOut;

    /// <summary>
    ///     Runs <c>jb</c> for a call the user made, with <paramref name="arguments" /> (whose first entry is the
    ///     subcommand) against the solution in <paramref name="config" />, and returns its result for the
    ///     caller's own post-checks.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Queues for the run slot and the lock, and throws on a non-zero exit or at the run cap.
    ///         <paramref name="onProgress" /> receives the run's advance as rendered lines while it runs: plain
    ///         strings, so no MCP type reaches this layer.
    ///     </para>
    ///     <para>
    ///         With a <paramref name="filter" />, which <paramref name="arguments" /> must name in place of the
    ///         solution, the slot, the lock, the seeding and the cache state still key off the solution, because
    ///         <c>jb</c> opens the cache generation of the solution a filter names. The filter is written once
    ///         that lease is held, the one lease its path belongs to (<see cref="CleanupFilter.PathFor" />).
    ///     </para>
    /// </remarks>
    public async Task<ProcessResult> RunAsync(
        ResolvedConfig config,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        Action<string>? onProgress = null,
        SolutionFilterPlan? filter = null)
    {
        var timedOut = false;
        try
        {
            // Armed before either queue rather than at the spawn: the waits for the slot and then for JbRunLock,
            // bounded by the run cap, can last a whole run with no jb in existence to stream, and the caller
            // still needs to hear from it. Its opening phase is the slot wait, the first thing this call does.
            await using JbRunProgress? progress = JbRunProgress.Reporting(
                arguments[0],
                config.SolutionPath,
                runTimeout,
                onProgress,
                logger,
                JbRunPhase.Turn,
                heartbeatInterval);

            // Entered before queueing, not after: a call arriving during a cold pre-warm would otherwise pay the
            // whole queue wait and then its own full run, which is strictly worse than never pre-warming.
            using IDisposable foreground = runYield.EnterForeground();

            // Timed here rather than read back from the lock: the run line reports what this call waited,
            // including the reclaim above (standing a pre-warm down costs the reap of a killed jb tree) and the
            // slot wait below.
            var queued = Stopwatch.StartNew();

            // The claim, the slot and the lease are scoped inside this try so all three are released before the
            // finally below announces a timeout: announced while any is still held, the re-armed pass would
            // settle as a skip and buy nothing, silently. Disposal runs in reverse declaration order, so the
            // lease goes first, then the slot, then the claim.
            //
            // Slot before lock, never the other way round: the lock's lease is cross-process, and holding it
            // while waiting on a resource local to this process would make another session queue behind our
            // own fan-out. The transplant runs inside both.
            using IDisposable slot = await runSlot.TakeAsync(arguments[0], config.SolutionPath, cancellationToken);

            progress?.Queued();
            using IDisposable runLease = await runLock.AcquireAsync(config.SolutionPath, config.CacheHome, cancellationToken);
            TimeSpan queueWait = queued.Elapsed;

            if (filter is not null) WriteFilter(filter);

            progress?.Seeding();
            bool seeded = await transplanter.TryTransplantAsync(config, cancellationToken);

            // Read here rather than inside the spawn, and after the transplant decision: this is the state jb
            // is about to open, and it is quoted both by the run line inside the spawn and by the timeout
            // message outside it.
            JbCacheState cache = JbCacheState.Read(config.SolutionPath, config.CacheHome, seeded, config.JbVersion, logger);

            ProcessResult result;
            try
            {
                result = await SpawnAsync(config, arguments, queueWait, ForACall, cache, progress, cancellationToken);
            }
            catch (ProcessTimeoutException exception)
            {
                timedOut = true;
                throw new UserErrorException(TimedOutMessage(arguments[0], progress?.FilesSeen ?? 0, cache, filter is not null), exception);
            }

            if (result.ExitCode != 0)
            {
                string tail = StandardErrorTail(result.StandardError);
                throw new JbExitCodeException(
                    $"jb {arguments[0]} exited with code {result.ExitCode}.\n{tail}", result.ExitCode, tail);
            }

            return result;
        }
        finally
        {
            if (timedOut) AnnounceTimeout(config);
        }
    }

    /// <summary>
    ///     Runs <c>jb</c> speculatively: only if no real call is in flight in this process, only if the cache
    ///     generation is free right now, and only until a real call wants it.
    /// </summary>
    /// <remarks>
    ///     No ending here is an error, so it returns which one it was rather than a result or a throw.
    /// </remarks>
    public async Task<SpeculativeRunOutcome> TryRunAsync(
        ResolvedConfig config,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        // Claimed before the lease is taken: the claim is what a caller the user is waiting on cancels, and
        // no claim means one is already in flight.
        using JbRunYield.SpeculativeRun? speculative = runYield.TryEnterSpeculative(cancellationToken);
        if (speculative is null) return SpeculativeRunOutcome.NotStarted;

        try
        {
            // Skip rather than queue, at the slot as at the lock. Holding the slot is what makes a call on
            // another solution, which cancels this pass, wait out the reap of the killed tree instead of
            // running beside it.
            using IDisposable? slot = runSlot.TryTake(arguments[0], config.SolutionPath);
            if (slot is null)
            {
                logger.LogDebug(
                    "Skipping speculative work on {SolutionPath}: another jb run of this server is already in flight",
                    config.SolutionPath);

                return SpeculativeRunOutcome.NotStarted;
            }

            using IDisposable? runLease = runLock.TryAcquire(config.SolutionPath, config.CacheHome);
            if (runLease is null)
            {
                logger.LogDebug(
                    "Skipping speculative work on {SolutionPath}: its ReSharper cache generation is already in use",
                    config.SolutionPath);

                return SpeculativeRunOutcome.NotStarted;
            }

            bool seeded = await transplanter.TryTransplantAsync(config, speculative.Token);

            // ProcessRunner calls process.Start() before it looks at its token, so a pre-warm cancelled in this
            // window would fork a jb only to tree-kill and reap it, holding for those seconds the lease the
            // foreground call is queueing for.
            speculative.Token.ThrowIfCancellationRequested();

            JbCacheState cache = JbCacheState.Read(
                config.SolutionPath, config.CacheHome, seeded, config.JbVersion, logger);

            // Zero queue wait: TryAcquire does not wait, so a lease in hand was uncontended. No progress
            // reporter: nobody is waiting on this run.
            ProcessResult result = await SpawnAsync(
                config, arguments, TimeSpan.Zero, Speculative, cache, null, speculative.Token);

            return result.ExitCode == 0 ? SpeculativeRunOutcome.Completed : SpeculativeRunOutcome.Failed;
        }
        catch (ProcessTimeoutException)
        {
            // Not a failure: ProcessRunner raises this only after killing the tree at the cap, while a jb that
            // decided something went wrong exits non-zero above. The cap protects a call the user is waiting on
            // from a stuck jb, and a cold solution big enough to exceed it is the one pre-warming exists for. The
            // generation is left part-built rather than untouched, so this is not NotStarted either.
            return SpeculativeRunOutcome.Capped;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A caller the user is waiting on took over. ProcessRunner surfaces that and our own caller's
            // cancellation identically, as an OCE on the token it was handed, so this filter is the only
            // discriminator.
            return SpeculativeRunOutcome.StoodDown;
        }
    }

    /// <summary>
    ///     Spawns <c>jb</c>, logging the run's opening line before it starts and a line for however it ends.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A clean exit, and only a clean exit, stamps the warm marker, clears any cold tombstone a reset
    ///         left (the cache this run rebuilt is the solution's own) and records what the run cost under the
    ///         band it started in. Every subcommand reaches them, since each analyses the whole solution into the
    ///         same cache generation. A run killed at the cap or one that exited non-zero reaches none of them.
    ///     </para>
    ///     <para>
    ///         The opening line is written before the spawn because the cache state and queue wait it carries
    ///         predict the duration about to follow and are unrecoverable afterwards. It is logged here rather
    ///         than in <see cref="ProcessRunner" />, which cannot tell a run from a version probe.
    ///     </para>
    /// </remarks>
    private async Task<ProcessResult> SpawnAsync(
        ResolvedConfig config,
        IReadOnlyList<string> arguments,
        TimeSpan queueWait,
        string runKind,
        JbCacheState cache,
        JbRunProgress? progress,
        CancellationToken cancellationToken)
    {
        string subcommand = arguments[0];

        // The same reading the opening line leads with, and for the same reason: it is what predicts the
        // minutes about to follow, and jb prints nothing for the opening stretch of them.
        progress?.Spawning(cache.Summary);

        logger.LogInformation(
            "jb {Subcommand} starting on {SolutionPath} ({RunKind}): {CacheState}, queued {QueueWaitMs} ms",
            subcommand,
            config.SolutionPath,
            runKind,
            cache.Summary,
            (long)queueWait.TotalMilliseconds);

        // Guarded because it is the one reading here that walks the whole tree, and the tree is hundreds of
        // megabytes.
        if (logger.IsEnabled(LogLevel.Debug) && cache.TryMeasure(config.CacheHome) is { } measured)
            logger.LogDebug(
                "Its cache generations hold {CacheBytes} bytes across {CacheFiles} files",
                measured.Bytes,
                measured.Files);

        var elapsed = Stopwatch.StartNew();
        ProcessResult result;
        try
        {
            Action<string>? onOutputLine = progress is null ? null : progress.OnOutputLine;

            result = await processRunner.RunAsync(
                config.JbExecutablePath, arguments, runTimeout, cancellationToken, onOutputLine);
        }
        catch (ProcessTimeoutException)
        {
            // How far it had got is what this line adds, and the only place it survives: without it a run
            // killed early and one killed near the end log the same line. It carries neither CacheState nor
            // ExitCode, the properties that mark the opening and exit lines, so a cap reads as neither of those.
            logger.LogInformation(
                "jb {Subcommand} was killed at the {RunCap} cap after {ElapsedMs} ms, having reached {FilesSeen} file(s)",
                subcommand,
                DurationFormatter.Format(runTimeout),
                elapsed.ElapsedMilliseconds,
                progress?.FilesSeen ?? 0);

            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation(
                "jb {Subcommand} was cancelled after {ElapsedMs} ms", subcommand, elapsed.ElapsedMilliseconds);

            throw;
        }

        logger.LogInformation(
            "jb {Subcommand} exited with code {ExitCode} after {ElapsedMs} ms",
            subcommand,
            result.ExitCode,
            elapsed.ElapsedMilliseconds);

        if (result.ExitCode != 0) return result;

        // The build is stamped alongside the generation: this run is what made the cache warm, so it is the
        // one thing that can say which jb warmed it, and the next run compares its own against it.
        StampOutcome stamped = JbWarmMarker.Stamp(config.SolutionPath, config.CacheHome, logger, config.JbVersion);
        if (stamped == StampOutcome.NoGenerationMatched) WarnOnceAboutUnrecognisedNaming(config);

        JbColdTombstone.Clear(config.SolutionPath, config.CacheHome, logger);

        // Keyed by the band the run started in, not the one it leaves: a transplant-fired run records what a
        // seeded run costs, not what the warm cache it has become would. The stopwatch is armed after the lease
        // and the transplant, so the queue wait is outside this figure, which is what makes it comparable to the
        // elapsed time a progress line names.
        if (cache.CostBand is { } band)
            JbCostRecord.Stamp(config.SolutionPath, config.CacheHome, band, elapsed.Elapsed, logger);

        return result;
    }

    /// <summary>
    ///     Warns, once per session, that a run succeeded without leaving a directory this server can recognise
    ///     as its cache generation.
    /// </summary>
    /// <remarks>
    ///     That means <c>jb</c>'s naming has moved away from what <see cref="JbSolutionCacheHash" /> reproduces.
    ///     Nothing breaks (every feature reading the name stops finding one), but it is the only signal that the
    ///     derivation has gone stale, so it is a warning rather than a debug line.
    /// </remarks>
    private void WarnOnceAboutUnrecognisedNaming(ResolvedConfig config)
    {
        if (Interlocked.Exchange(ref _driftWarned, 1) != 0) return;

        logger.LogWarning(
            "A jb run against solution {SolutionPath} succeeded but left no cache generation directory matching its computed hash under {CacheHome}; "
            + "features that need to name a cache generation are disabled for this server",
            config.SolutionPath,
            config.CacheHome);
    }

    /// <summary>Writes the filter a cleanup runs through, under the lease the caller holds.</summary>
    /// <remarks>
    ///     A failure ends the call before any <c>jb</c> starts, naming the switch that takes the filter out of
    ///     the path altogether, since nothing about a retry would change a directory that cannot be written.
    /// </remarks>
    private void WriteFilter(SolutionFilterPlan filter)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filter.Path)!);
            File.WriteAllText(filter.Path, filter.Content);
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            throw new UserErrorException(
                $"Could not write the solution filter this cleanup runs through to \"{filter.Path}\" "
                + $"({ConfigWarningBanner.SingleLine(exception.Message)}). To clean up through the whole solution "
                + $"instead, set {CleanupFilter.EnableVariable}=off "
                + InEnvBlockThenRestart,
                exception);
        }

        logger.LogDebug("Wrote the solution filter {FilterPath}", filter.Path);
    }

    /// <summary>
    ///     Composes what a foreground caller is told when its run hits the cap.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Composed here rather than left to <see cref="ProcessRunner" />, which knows neither that the cap
    ///         is this server's nor the variable that moves it.
    ///     </para>
    ///     <para>
    ///         The cause and the remedy depend on the <paramref name="cache" /> state the run opened. Any state
    ///         but warm is read as the cold first run, which a retry resumes, and which scoping cannot shorten
    ///         except through a filter (<paramref name="throughFilter" />), where the run loads only its files'
    ///         projects. A warm run was building no cache, so it is promised no resumption: a warm <c>inspectcode</c>
    ///         is a whole-solution analysis that outgrew the cap, which only raising the cap cures, while a warm
    ///         <c>cleanupcode</c>'s time grows with the files it rewrites, so narrowing them is the first remedy.
    ///         Every <c>cleanupcode</c> is warned that files may already be rewritten, whatever the cache state:
    ///         a cold run analyses before it rewrites, but nothing here knows which side of that the cap fell on.
    ///     </para>
    ///     <para>
    ///         <paramref name="filesSeen" /> is how many files <c>jb</c> had reported reaching, zero when nothing
    ///         said. That a retry resumes rather than starting over is the one claim here this server cannot
    ///         verify, so the count is offered as evidence for it and withheld when there is none, which is always
    ///         the case for <c>cleanupcode</c>, whose per-file output nothing here can count. It is spelled by
    ///         <see cref="RunProgressFormatter.Files" />, so it reads as it did on the progress line the caller
    ///         just watched.
    ///     </para>
    ///     <para>
    ///         <paramref name="cache" /> also supplies how long the last comparable run of this solution took,
    ///         which turns the cap into evidence for or against raising it. The figure is left out when no
    ///         comparable run has finished, which a warm state never has.
    ///     </para>
    /// </remarks>
    private string TimedOutMessage(string subcommand, int filesSeen, JbCacheState cache, bool throughFilter)
    {
        string cap = $"jb {subcommand} timed out after {DurationFormatter.Format(runTimeout)} and was stopped.\n"
                     + $"That cap is this server's, not jb's own: raise it by setting {JbRunTimeout.Variable} (in seconds) "
                     + InEnvBlockThenRestart
                     + "\n";

        if (cache.Kind == JbCacheKind.Warm)
            return subcommand == CleanupService.Subcommand
                ? cap
                  + "The ReSharper cache was warm, so this was not the slow first run. A cleanup's time grows with the "
                  + "number of files it rewrites, so narrow `files` to the ones that changed, or split them across "
                  + "several calls, before raising the cap. "
                  + RewrittenWarning
                : cap
                  + "The ReSharper cache was warm, so this was not the slow first run: a warm analysis of this "
                  + "solution takes longer than the cap, and raising the cap is the fix. "
                  + ScopingCannotHelp;

        string reached = filesSeen > 0
            ? $"jb had reached {RunProgressFormatter.Files(filesSeen)} by the time it was stopped, and the cache keeps that work, so a "
              + "retry resumes from there rather than starting over."
            : "The cache keeps most of what this run built, so a retry resumes rather than starting over.";

        string recorded = cache.QuotableCost is { } quotable
            ? $"The last {JbCostRecord.Label(quotable.Band)} run of this solution took {DurationFormatter.Format(quotable.Cost)}. "
            : string.Empty;

        string rewritten = subcommand == CleanupService.Subcommand
            ? " " + RewrittenWarning
            : string.Empty;

        string scoping = throughFilter
            ? string.Empty
            : ScopingCannotHelp + " ";

        return cap
               + "A run that long is almost always a cold ReSharper cache. "
               + recorded
               + scoping
               + reached
               + rewritten;
    }

    /// <summary>
    ///     Tells whoever is listening that a foreground run ran out of budget.
    /// </summary>
    /// <remarks>
    ///     Swallows anything a subscriber throws: this fires while the <see cref="UserErrorException" />
    ///     carrying <see cref="TimedOutMessage" /> is unwinding, and a throwing listener would replace the one
    ///     message that tells the user whose cap it was with an unrelated failure.
    /// </remarks>
    private void AnnounceTimeout(ResolvedConfig config)
    {
        try
        {
            ForegroundRunTimedOut?.Invoke(config);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "A listener for the run-timed-out signal threw; the timeout itself is still reported to the caller");
        }
    }

    /// <summary>
    ///     Appends the config-derived options every <c>jb</c> subcommand takes, in one place.
    /// </summary>
    /// <remarks>
    ///     Every subcommand must pass identical configuration to open the same cache generation, so the options
    ///     are appended here once. The optional values are null-or-meaningful by <c>ConfigResolver</c>'s
    ///     contract, so presence is a null check, except <c>--settings</c>, which is present only for a file
    ///     <c>jb</c> cannot discover itself: it mounts a Custom layer above the whole stack, so passing a
    ///     discovered file would demote every project's own <c>.csproj.DotSettings</c> rather than change nothing.
    /// </remarks>
    internal static void AppendConfigArguments(List<string> arguments, ResolvedConfig config)
    {
        arguments.Add($"--caches-home={config.CacheHome}");

        if (config.SettingsPathIsCustomLayer) arguments.Add($"--settings={config.SettingsPath}");

        if (config.Extensions is not null) arguments.Add($"-x={config.Extensions}");

        if (config.ExtensionSource is not null) arguments.Add($"--source={config.ExtensionSource}");
    }

    /// <summary>
    ///     Builds the <c>--include</c> argument: one argument joining the patterns with <c>;</c>, each made
    ///     relative to <paramref name="solutionDirectory" /> by <see cref="FilePathList.ToIncludePattern" />.
    /// </summary>
    /// <remarks>
    ///     The translation is made here, at the <c>jb</c>-contract boundary, rather than at the tool edge.
    /// </remarks>
    internal static string IncludeArgument(IReadOnlyList<string> files, string solutionDirectory)
    {
        IEnumerable<string> patterns = files.Select(file => FilePathList.ToIncludePattern(file, solutionDirectory));
        return IncludeArgument(patterns);
    }

    /// <summary>
    ///     Builds the <c>--include</c> argument over <paramref name="patterns" />, each already spelt for <c>jb</c>.
    /// </summary>
    internal static string IncludeArgument(IEnumerable<string> patterns)
    {
        return $"--include={string.Join(";", patterns)}";
    }

    /// <summary>
    ///     Returns the last <see cref="StandardErrorTailLength" /> characters of
    ///     <paramref name="standardError" />, trailing whitespace trimmed: enough of a failed run's output to
    ///     diagnose it without flooding the response.
    /// </summary>
    /// <remarks>
    ///     Tolerates the null a defaulted <see cref="ProcessResult" /> carries: the paths that quote a tail exist
    ///     to report a failure, not to add one.
    /// </remarks>
    internal static string StandardErrorTail(string? standardError)
    {
        string trimmed = standardError?.TrimEnd() ?? string.Empty;
        return trimmed.Length <= StandardErrorTailLength ? trimmed : trimmed[^StandardErrorTailLength..];
    }
}