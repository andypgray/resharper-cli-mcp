using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Formatting;

namespace Zphil.ReSharperCli.Services;

/// <summary>
///     A <c>jb</c> run that exited non-zero, carrying what it exited with.
/// </summary>
/// <remarks>
///     A distinct type so a caller can recognise this particular failure and restate it with knowledge
///     <see cref="JbRunner" /> does not have — the same bargain <see cref="ProcessTimeoutException" /> makes
///     one level down. It is needed as a discriminator rather than for its payload alone: the timeout path
///     throws a <see cref="UserErrorException" /> too, and its message must be left exactly as it is, because
///     a cleanup killed at the cap <em>may</em> have rewritten files and cannot claim otherwise.
/// </remarks>
internal sealed class JbExitCodeException(string message, int exitCode, string standardErrorTail)
    : UserErrorException(message)
{
    /// <summary>The code <c>jb</c> exited with. Never zero.</summary>
    public int ExitCode { get; } = exitCode;

    /// <summary>
    ///     The tail of the run's standard error, already bounded by
    ///     <see cref="JbRunner.StandardErrorTail" />, so a caller restating the failure quotes what this
    ///     message quotes rather than re-trimming it.
    /// </summary>
    public string StandardErrorTail { get; } = standardErrorTail;
}

/// <summary>
///     The one path by which a <c>jb</c> subcommand is run: it takes the cross-process
///     <see cref="JbRunLock" /> for the solution's cache generation, spawns <c>jb</c> under the run
///     timeout, and turns a non-zero exit into a <see cref="JbExitCodeException" /> quoting the tail of
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
///         reports rather than throws. What it reports is a <see cref="SpeculativeRunOutcome" /> naming
///         <em>which</em> of those it did, so a caller summarising a pass cannot contradict the run lines
///         underneath it. Both go through <see cref="SpawnAsync" />, which keeps this class the
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
/// <param name="heartbeatInterval">
///     How often a run in flight reports itself, defaulting to
///     <see cref="JbRunProgress.HeartbeatInterval" />. A parameter for the same reason that default's own
///     constructor leaves one open: an integration test driving the whole pipeline should not have to wait
///     ten seconds to see a second beat.
/// </param>
internal sealed class JbRunner(
    IProcessRunner processRunner,
    JbRunLock runLock,
    JbRunYield runYield,
    CacheTransplanter transplanter,
    TimeSpan runTimeout,
    ILogger<JbRunner> logger,
    TimeSpan? heartbeatInterval = null)
{
    private const int StandardErrorTailLength = 2000;

    /// <summary>How the two entry points name themselves in the run lines below.</summary>
    private const string ForACall = "for a call";

    private const string Speculative = "speculative";

    /// <summary>
    ///     Whether this server session has already said that <c>jb</c>'s cache-generation naming no longer
    ///     matches what <see cref="JbSolutionCacheHash" /> reproduces. One fact about this machine's
    ///     <c>jb</c> rather than one fact per run, so repeating it on every successful run would bury a
    ///     session's log in the same sentence.
    /// </summary>
    /// <remarks>
    ///     An instance field, and that is the whole reason the latch lives in this class rather than beside
    ///     the marker it describes: this is registered once per host, so one instance is one server session
    ///     and "once" is scoped to the same session as the <see cref="ILogger" /> the warning lands in. A
    ///     static latch is exact only while a process holds one session, and lets the first session in a
    ///     process that holds several absorb the warning every later one had to say for itself.
    /// </remarks>
    private int _driftWarned;

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
    /// <remarks>
    ///     <paramref name="onProgress" /> is where the run's advance is reported while it is still running —
    ///     trailing and optional for the same reason <see cref="IProcessRunner.RunAsync" />'s line observer
    ///     is, since most callers have nowhere to report to. Already-rendered lines rather than structured
    ///     state, so the MCP types a notification is eventually built from stop at the tool surface.
    /// </remarks>
    public async Task<ProcessResult> RunAsync(
        ResolvedConfig config,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        Action<string>? onProgress = null)
    {
        var timedOut = false;
        try
        {
            // Armed before the queue rather than at the spawn, and that placement is the point: JbRunLock's
            // wait is bounded by the run cap, so a caller can sit here for ten minutes with no jb in
            // existence to stream. Streaming jb's output alone could never have covered this stretch, and it
            // is exactly the stretch a second raw jb against the same cache creates.
            await using JbRunProgress? progress = JbRunProgress.Reporting(
                arguments[0], config.SolutionPath, runTimeout, onProgress, logger, heartbeatInterval);

            // Entered before queueing, not after: a call arriving ten seconds into a cold pre-warm would
            // otherwise pay the whole queue wait and then its own full run, which is strictly worse than
            // never pre-warming.
            using IDisposable foreground = runYield.EnterForeground();

            // Timed here rather than read back from the lock, because what the run line reports is what this
            // call waited — the reclaim above it included, since standing a pre-warm down costs the reap of a
            // killed jb tree and that time is just as much the caller's as the queue is.
            var queued = Stopwatch.StartNew();

            // Both scoped inside this try on purpose, so the lease is released and the claim stood down
            // before the finally below announces a timeout. Announce while still holding either and the
            // re-armed pass would settle as a skip — the re-arm would buy nothing, silently. Disposal is the
            // reverse of declaration, so the lease goes first and the count outlives it by a hair.
            using IDisposable runLease = await runLock.AcquireAsync(config.SolutionPath, config.CacheHome, cancellationToken);
            TimeSpan queueWait = queued.Elapsed;

            progress?.Seeding();
            bool seeded = await transplanter.TryTransplantAsync(config, cancellationToken);

            // Read here rather than inside the spawn, and after the transplant decision: this is the state jb
            // is about to open, and the two endings that quote it — the run line and the timeout message —
            // both sit outside the frame that used to own the reading.
            JbCacheState cache = JbCacheState.Read(config.SolutionPath, config.CacheHome, seeded, logger);

            ProcessResult result;
            try
            {
                result = await SpawnAsync(config, arguments, queueWait, ForACall, cache, progress, cancellationToken);
            }
            catch (ProcessTimeoutException exception)
            {
                timedOut = true;
                throw new UserErrorException(TimedOutMessage(arguments[0], progress?.FilesSeen ?? 0, cache), exception);
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
    ///     Run <c>jb</c> speculatively: only if no real call is in flight in this process, only if the cache
    ///     generation is free right now, and only until a real call wants it. Names how the pass ended rather
    ///     than reporting a result, because no ending here is an error and the caller's whole job is to tell
    ///     them apart — a run given up after minutes of analysis is not the run that never started. A non-zero
    ///     exit is one of those endings rather than a throw, since background work has no channel to raise a
    ///     failure through and its caller decides what a failure means.
    /// </summary>
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
            using IDisposable? runLease = runLock.TryAcquire(config.SolutionPath, config.CacheHome);
            if (runLease is null)
            {
                logger.LogDebug(
                    "Skipping speculative work on {SolutionPath}: its ReSharper cache generation is already in use",
                    config.SolutionPath);

                return SpeculativeRunOutcome.NotStarted;
            }

            bool seeded = await transplanter.TryTransplantAsync(config, speculative.Token);

            // ProcessRunner calls process.Start() before it ever looks at its token, so a pre-warm cancelled
            // in this window would fork a jb only to tree-kill and reap it — holding, for those seconds, the
            // very lease the real call is queueing for. Cheap to check, and re-arming makes it reachable more
            // than once per process.
            speculative.Token.ThrowIfCancellationRequested();

            JbCacheState cache = JbCacheState.Read(config.SolutionPath, config.CacheHome, seeded, logger);

            // Zero queue wait by construction: TryAcquire does not wait, so a lease in hand was uncontended.
            // No progress either: nobody is waiting on this run, so there is nobody to report it to.
            ProcessResult result = await SpawnAsync(
                config, arguments, TimeSpan.Zero, Speculative, cache, null, speculative.Token);

            return result.ExitCode == 0 ? SpeculativeRunOutcome.Completed : SpeculativeRunOutcome.Failed;
        }
        catch (ProcessTimeoutException)
        {
            // Not a failure, and the exception type is what says so: ProcessRunner raises this one only after
            // killing the tree at the cap, while a jb that decided something went wrong exits non-zero above.
            // The cap is a foreground protection — it is there so a call the user is waiting on cannot hang on
            // a stuck jb — and nobody is waiting on this one, so a cold solution big enough to exceed it is
            // precisely the solution pre-warming exists for. It leaves the generation part-built rather than
            // untouched, which is why it is also not the ending that reports nothing happened.
            return SpeculativeRunOutcome.Capped;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A caller the user is waiting on took over. ProcessRunner surfaces that and our own caller's
            // cancellation identically — an OCE on the token it was handed — so this filter is the only
            // discriminator. It catches the two reclaims that land before jb starts as well as the one that
            // kills it mid-run, and reports all three the same way on purpose: JbRunYield records every
            // reclaim at Information, and a transplant can copy for minutes before the guard above is reached.
            return SpeculativeRunOutcome.StoodDown;
        }
    }

    /// <summary>
    ///     The single <c>jb</c> spawn. A clean exit stamps the warm marker, so the pre-warm debounce records
    ///     every successful run — foreground tool calls included, and <c>cleanupcode</c> as much as
    ///     <c>inspectcode</c>, since both analyse the whole solution into the same cache generation — rather
    ///     than relying on one call site remembering to. The same exit discharges any cold tombstone a reset
    ///     left: the cache this run rebuilt is the solution's own, so there is no longer a reset to protect.
    ///     It also records what the run cost under the band it started in, which is what lets the next run
    ///     like it say how long a run like it takes. All three are the clean exit's alone — a run killed at
    ///     the cap or one that exited non-zero reaches none of them.
    /// </summary>
    /// <remarks>
    ///     It is also where the pair of <c>Information</c> lines a <c>jb</c> run costs the log are written,
    ///     one before and one after, and both halves earn their place. The opening line is what makes a run
    ///     legible <em>while</em> it is happening: a <c>jb</c> run is minutes of silence, and a run that is
    ///     killed or never returns would otherwise leave nothing at all behind — the exact "starts, never
    ///     ends" shape the pre-warm's own logging used to have. It also carries the two facts that predict the
    ///     duration about to follow, the cache state and the queue wait, which are unrecoverable afterwards.
    ///     Placed here rather than in <see cref="ProcessRunner" /> because this is the frame that knows a run
    ///     from a version probe, and one level down they are the same spawn.
    ///     <para>
    ///         The stamp is also where naming drift is met, and this class — one instance per server session —
    ///         is what owns the once-per-session latch behind
    ///         <see cref="WarnOnceAboutUnrecognisedNaming" />. <see cref="JbWarmMarker" /> reports the
    ///         condition and says nothing, so the warning is scoped to the session whose log it lands in.
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
        // minutes about to follow, and jb says nothing at all for the first half-minute of them.
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
            // How far it had got is the fact this line exists to add, and the only place it survives: a run
            // killed having analysed forty files and one killed at 1,200 are otherwise the same line. The
            // properties are chosen around the two lines JbRunLoggingTests locates by name — no CacheState
            // here, no ExitCode — so that a cap is still identifiable as neither of the other two endings.
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

        if (JbWarmMarker.Stamp(config.SolutionPath, config.CacheHome, logger) == StampOutcome.NoGenerationMatched)
            WarnOnceAboutUnrecognisedNaming(config);

        JbColdTombstone.Clear(config.SolutionPath, config.CacheHome, logger);

        // Keyed by the band the run STARTED in — a transplant-fired run records what a seeded run costs, not
        // what the warm cache it has just become would. The stopwatch is armed after the lease and the
        // transplant, so the queue wait is outside this figure by construction, which is what makes it
        // comparable to the elapsed time a progress line names.
        if (cache.CostBand is { } band)
            JbCostRecord.Stamp(config.SolutionPath, config.CacheHome, band, elapsed.Elapsed, logger);

        return result;
    }

    /// <summary>
    ///     A run succeeded and left no directory this server can recognise as its cache generation, so
    ///     <c>jb</c>'s naming has moved away from what <see cref="JbSolutionCacheHash" /> reproduces. Nothing
    ///     is broken by it — every feature reading the name simply stops finding one — but it is the single
    ///     signal that the derivation has gone stale, so it is a warning rather than a debug line, and said
    ///     once for as long as this session lasts.
    /// </summary>
    private void WarnOnceAboutUnrecognisedNaming(ResolvedConfig config)
    {
        if (Interlocked.Exchange(ref _driftWarned, 1) != 0) return;

        logger.LogWarning(
            "A jb run against solution {SolutionPath} succeeded but left no cache generation directory matching its computed hash under {CacheHome}; "
            + "features that need to name a cache generation are disabled for this server",
            config.SolutionPath,
            config.CacheHome);
    }

    /// <summary>
    ///     What a foreground caller is told when its run hit the cap. Three things it cannot work out for
    ///     itself, and the reason this message is not left to <see cref="ProcessRunner" />: the cap belongs
    ///     to this server rather than to <c>jb</c> or to the MCP client, there is an environment variable
    ///     that moves it, and the obvious next move does not work — scoping with <c>files</c> narrows what
    ///     <c>jb</c> reports and never what it analyses, so a retry scoped to one file is just as slow.
    /// </summary>
    /// <param name="subcommand">The <c>jb</c> subcommand that ran out of budget.</param>
    /// <param name="filesSeen">
    ///     How many files <c>jb</c> had reported reaching, or zero when nothing said. The promise that a
    ///     retry "resumes rather than starting over" is the one claim here this server cannot verify, and
    ///     until there was a count it was made with confidence it had not earned. A number is offered as
    ///     evidence for it and withheld when there is none — which is also what keeps the claim honest for
    ///     <c>cleanupcode</c>, whose per-file output nothing here can count. Spelled by
    ///     <see cref="RunProgressFormatter.Files" />, so the count reads here as it read on the progress
    ///     line the caller just watched.
    /// </param>
    /// <param name="cache">
    ///     What the cache looked like going in, for the one figure this message cannot otherwise offer: how
    ///     long a comparable run of this solution took. It turns "the cap was ten minutes" into evidence for
    ///     or against raising it — a solution recorded at eight minutes cold will fit in twelve, and one that
    ///     has never finished will not. Absent whenever no comparable run has, in which case the message is
    ///     what it always was.
    /// </param>
    private string TimedOutMessage(string subcommand, int filesSeen, JbCacheState cache)
    {
        string reached = filesSeen > 0
            ? $"jb had reached {RunProgressFormatter.Files(filesSeen)} by the time it was stopped, and the cache keeps that work, so a "
              + "retry resumes from there rather than starting over."
            : "The cache keeps most of what this run built, so a retry resumes rather than starting over.";

        string recorded = cache.CostBand is { } band && cache.LastComparableCost is { } cost
            ? $"The last {JbCostRecord.Label(band)} run of this solution took {DurationFormatter.Format(cost)}. "
            : string.Empty;

        return $"jb {subcommand} timed out after {DurationFormatter.Format(runTimeout)} and was stopped.\n"
               + $"That cap is this server's, not jb's own: raise it by setting {JbRunTimeout.Variable} (in seconds) "
               + "in this server's env block in your MCP client config, then restart the server.\n"
               + "A run that long is almost always a cold ReSharper cache. "
               + recorded
               + "Scoping the next call with `files` will not help — jb analyses the whole solution whatever "
               + "the report is narrowed to. "
               + reached;
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
            logger.LogWarning(exception, "A listener for the run-timed-out signal threw; the timeout itself is still reported to the caller");
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

    /// <summary>
    ///     The <c>--include</c> flag: jb takes one argument joining the patterns with <c>;</c>, each of them
    ///     relative to <paramref name="solutionDirectory" /> — see
    ///     <see cref="FilePathList.ToIncludePattern" /> for why an absolute one cannot be passed through.
    /// </summary>
    /// <remarks>
    ///     The translation is made here rather than at the tool edge for the same reason the config tail is
    ///     appended here: this is the jb-contract boundary, and it reaches inspect and cleanup at once instead
    ///     of being a rule two call sites keep by convention until someone adds a third.
    /// </remarks>
    internal static string IncludeArgument(IReadOnlyList<string> files, string solutionDirectory)
    {
        IEnumerable<string> patterns = files.Select(file => FilePathList.ToIncludePattern(file, solutionDirectory));
        return $"--include={string.Join(";", patterns)}";
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