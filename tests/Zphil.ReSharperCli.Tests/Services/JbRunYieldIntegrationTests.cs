using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Services;

/// <summary>
///     Drives <see cref="JbRunYield" /> through a <see cref="JbRunner" /> and a <see cref="CacheResetService" />
///     wired to one shared yield; <see cref="Execution.JbRunYieldTests" /> drives the yield on its own.
/// </summary>
/// <remarks>
///     <para>
///         Both kinds of caller are driven here, against one <see cref="JbRunYield" />, because that sharing
///         is invisible from either side alone: a <see cref="JbRunner" /> and a <see cref="CacheResetService" />
///         wired to yields of their own compile, pass every test here that drives only one kind of caller, and
///         arbitrate against nothing. <see cref="JbRunners" /> assembles the pair for the same reason the
///         composition root does.
///     </para>
///     <para>
///         The <see cref="YieldProbe" /> stands in for <c>jb</c> and makes every one of these assertions an
///         observation rather than a timing guess. The one sleep is <see cref="ReapWindow" />, a watch for a run
///         or a delete that must not happen, which raises no event to wait on.
///     </para>
/// </remarks>
public sealed class JbRunYieldIntegrationTests : IDisposable
{
    /// <summary>
    ///     A short wait cap, so a regression that stopped the pre-warm yielding fails these tests promptly
    ///     instead of hanging them out to the production cap.
    /// </summary>
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(10);

    /// <summary>How long a test watches for a run that must not happen while a killed pass is reaped.</summary>
    private static readonly TimeSpan ReapWindow = TimeSpan.FromMilliseconds(200);

    private static readonly string[] WarmUpArguments = ["inspectcode", "/sln/App.sln"];
    private static readonly string[] ForegroundArguments = ["inspectcode", "/sln/App.sln", "--include=src/A.cs"];

    private readonly string _cacheHome;
    private readonly ResolvedConfig _config;
    private readonly FakeEnvironment _environment = new();
    private readonly YieldProbe _probe = new();
    private readonly CacheResetService _reset;
    private readonly JbRunner _runner;

    public JbRunYieldIntegrationTests()
    {
        _cacheHome = _environment.CreateTempDirectory();
        _config = Configs.Bare("/sln/App.sln", _cacheHome);

        JbRunLock runLock = JbRunners.Lock(Cap);
        JbRunYield runYield = JbRunners.Yield();

        _runner = JbRunners.Create(_probe, runLock, runYield, Cap);
        _reset = JbRunners.Reset(runLock, runYield);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _probe.ReleaseAll();
        _environment.Dispose();
    }

    [Fact]
    public async Task ForegroundRun_ArrivingDuringAPreWarm_ReclaimsTheCacheGeneration()
    {
        // Arrange — a pre-warm holding the lease and mid-analysis.
        Task<SpeculativeRunOutcome> preWarm = _runner.TryRunAsync(_config, WarmUpArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);

        // Act — the foreground call cancels it on entry, so the lease frees up and this run gets to start.
        Task<ProcessResult> foreground = _runner.RunAsync(_config, ForegroundArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);
        _probe.ReleaseAll();

        // Assert — and the abandoned pre-warm says it stood down rather than that it never ran: nothing went
        // wrong, but a jb did minutes of work here, and the word for having done none belongs to a pass that
        // never started.
        (await foreground).ExitCode.ShouldBe(0);
        (await preWarm).ShouldBe(SpeculativeRunOutcome.StoodDown);
        _probe.Cancelled.ShouldBe(1);
    }

    [Fact]
    public async Task ForegroundRun_WithNoPreWarmInFlight_RunsUntouched()
    {
        // Arrange
        _probe.ReleaseAll();

        // Act
        ProcessResult result = await _runner.RunAsync(_config, ForegroundArguments, Ct);

        // Assert — the cancel-on-entry costs a call nothing when there is nothing to cancel.
        result.ExitCode.ShouldBe(0);
        _probe.Runs.ShouldBe(1);
        _probe.Cancelled.ShouldBe(0);
    }

    [Fact]
    public async Task PreWarmThatAlreadyFinished_IsNotCancelledRetroactively()
    {
        // Arrange — a pre-warm run to completion. Its token source is never disposed, so a later call that
        // still held a reference to it could cancel it after the fact; withdrawing it as the run ends is
        // what stops that.
        _probe.ReleaseAll();
        (await _runner.TryRunAsync(_config, WarmUpArguments, Ct)).ShouldBe(SpeculativeRunOutcome.Completed);
        CancellationToken preWarmToken = _probe.Tokens.ShouldHaveSingleItem();

        // Act
        ProcessResult result = await _runner.RunAsync(_config, ForegroundArguments, Ct);

        // Assert
        result.ExitCode.ShouldBe(0);
        preWarmToken.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task PreWarm_StartingWhileAForegroundRunIsInFlight_NeverRunsAtAll()
    {
        // Arrange — the degenerate ordering the trigger permits: a client whose first message is a tool call,
        // so the foreground run passes its cancel point before the pre-warm has anything to hand back. Left alone,
        // that call would then queue behind a pre-warm started a moment later — the one way pre-warming could
        // delay a call inside this process.
        Task<ProcessResult> foreground = _runner.RunAsync(_config, ForegroundArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);

        // Act
        SpeculativeRunOutcome preWarm = await _runner.TryRunAsync(_config, WarmUpArguments, Ct);

        // Assert — it stands down rather than racing: a real run analyses the same solution into the same
        // cache generation, so while one is in flight there is nothing for a speculative run to buy. And this
        // pass did nothing, which is the case NotStarted is reserved for.
        preWarm.ShouldBe(SpeculativeRunOutcome.NotStarted);
        _probe.Runs.ShouldBe(1);

        _probe.ReleaseAll();
        (await foreground).ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task PreWarm_StartingAfterAForegroundRunHasFinished_RunsAgain()
    {
        // Arrange — the same ordering a moment later, and the answer is the opposite one. Standing down while
        // a call is in flight is the invariant worth keeping; staying down for the rest of the process, as a
        // latch would, switches the pre-warm off when a call that has just hit the cap most needs it.
        _probe.ReleaseAll();
        await _runner.RunAsync(_config, ForegroundArguments, Ct);

        // Act
        SpeculativeRunOutcome preWarm = await _runner.TryRunAsync(_config, WarmUpArguments, Ct);

        // Assert
        preWarm.ShouldBe(SpeculativeRunOutcome.Completed);
        _probe.Runs.ShouldBe(2);
    }

    [Fact]
    public async Task TwoForegroundRunsRacingOnePreWarm_BothComplete()
    {
        // Arrange
        Task<SpeculativeRunOutcome> preWarm = _runner.TryRunAsync(_config, WarmUpArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);

        // Act — only one of the two can win the pre-warm's token source; the loser must find nothing rather
        // than trip over a half-cleared field, and both must still get their runs.
        Task<ProcessResult> first = _runner.RunAsync(_config, ForegroundArguments, Ct);
        Task<ProcessResult> second = _runner.RunAsync(_config, ForegroundArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);
        _probe.ReleaseAll();

        // Assert
        (await first).ExitCode.ShouldBe(0);
        (await second).ExitCode.ShouldBe(0);
        (await preWarm).ShouldBe(SpeculativeRunOutcome.StoodDown);
        _probe.Cancelled.ShouldBe(1);
    }

    [Fact]
    public async Task ForegroundRun_OnAnotherSolution_StandsThePreWarmDownAndWaitsForItsReap()
    {
        // Arrange — the precedence is process-wide, so a call against one solution reclaims from a pre-warm
        // of another. The bound on jb processes is too, which is what makes the wait below a wait rather
        // than a second run beside the dying one.
        _probe.HoldReap = true;
        Task<SpeculativeRunOutcome> preWarm = _runner.TryRunAsync(_config, WarmUpArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);

        // Act
        ResolvedConfig other = Configs.Bare(_environment.CreateSolutionPath("Other.sln"), _cacheHome);
        Task<ProcessResult> foreground = _runner.RunAsync(other, ForegroundArguments, Ct);
        await _probe.Dying.WaitAsync(Ct);

        // Assert — while the killed pass is being reaped, the foreground run waits instead of running beside
        // it. The lock cannot be the explanation: two solutions are two cache generations, and it lets both
        // through uncontended. A negative window, since a run that never starts raises no event.
        await Task.Delay(ReapWindow, Ct);
        _probe.Runs.ShouldBe(1);

        _probe.Reap();
        await _probe.WaitForNextStartAsync(Ct);
        _probe.ReleaseAll();
        (await foreground).ExitCode.ShouldBe(0);
        (await preWarm).ShouldBe(SpeculativeRunOutcome.StoodDown);
        _probe.Cancelled.ShouldBe(1);
    }

    [Fact]
    public async Task Reset_OnAnotherSolution_DoesNotQueueBehindThisServersRun()
    {
        // Arrange — the slot bounds jb processes, and a reset spawns none, so it must not be charged one.
        // Parked on a solution of its own, this run holds the slot for as long as the fixture lives.
        Task<ProcessResult> parked = _runner.RunAsync(_config, ForegroundArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);

        ResolvedConfig other = Configs.Bare(_environment.CreateSolutionPath("Other.sln"), _cacheHome);
        string generation = CacheHomes.PlantGenerationFor(_cacheHome, other.SolutionPath);

        // Act — bounded, because the failure this guards is a reset that queues rather than a wrong answer:
        // charged a slot it would sit behind a run nothing releases and hang rather than fail.
        CacheResetOutcome outcome = await _reset.RunAsync(other, Ct).WaitAsync(Cap, Ct);

        // Assert
        outcome.Dropped.ShouldBe([Path.GetFileName(generation)]);
        _probe.ReleaseAll();
        (await parked).ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task Reset_ArrivingDuringAPreWarm_ReclaimsTheCacheGeneration()
    {
        // Arrange — a pre-warm holding the lease and mid-analysis. The probe is never released, so nothing
        // below can be explained by the pass finishing on its own: left to queue, this reset would wait out
        // the whole cap and then fail.
        string generation = CacheHomes.PlantGenerationFor(_cacheHome, _config.SolutionPath);
        Task<SpeculativeRunOutcome> preWarm = _runner.TryRunAsync(_config, WarmUpArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);

        // Act
        CacheResetOutcome outcome = await _reset.RunAsync(_config, Ct);

        // Assert — a reset runs no jb of its own, which is why the precedence cannot live in the class that
        // runs jb.
        outcome.Dropped.ShouldBe([Path.GetFileName(generation)]);
        Directory.Exists(generation).ShouldBeFalse();
        (await preWarm).ShouldBe(SpeculativeRunOutcome.StoodDown);
        _probe.Cancelled.ShouldBe(1);
    }

    [Fact]
    public async Task PreWarm_StartingWhileAResetIsInFlight_NeverRunsAtAll()
    {
        // Arrange — a reset has no seam of its own to park on, so park it on a lock another session holds.
        // Its claim is raised before RunAsync's first await, so having the task in hand is proof it is up.
        FileStream otherSession = CacheHomes.HoldLockFile(_cacheHome, _config.SolutionPath);
        Task<CacheResetOutcome> reset = _reset.RunAsync(_config, Ct);

        SpeculativeRunOutcome preWarm;
        try
        {
            // Act — aimed at a second solution, whose own lock nothing holds. Bounded, because the failure
            // this guards is not a wrong answer but a pre-warm that runs: it would park on the probe, which
            // nothing releases until the fixture is torn down, and hang rather than fail.
            ResolvedConfig other = Configs.Bare("/sln/Other.sln", _cacheHome);
            preWarm = await _runner.TryRunAsync(other, WarmUpArguments, Ct).WaitAsync(Cap, Ct);
        }
        finally
        {
            // Released here rather than at scope exit: the reset is queued on this lock, so awaiting it while
            // still holding the file would be a deadlock only the fixture's cap would break.
            await otherSession.DisposeAsync();
        }

        await reset;

        // Assert — the lock cannot be the explanation, so the claim is the only thing left. A pre-warm must
        // never rebuild a cache while the call to drop it is in flight.
        preWarm.ShouldBe(SpeculativeRunOutcome.NotStarted);
        _probe.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task Reset_WithNoPreWarmInFlight_RunsUntouched()
    {
        // Arrange
        string generation = CacheHomes.PlantGenerationFor(_cacheHome, _config.SolutionPath);

        // Act
        CacheResetOutcome outcome = await _reset.RunAsync(_config, Ct);

        // Assert — entering the claim costs a reset nothing when there is nothing to stand down.
        outcome.Dropped.ShouldBe([Path.GetFileName(generation)]);
        _probe.Runs.ShouldBe(0);
        _probe.Cancelled.ShouldBe(0);
    }

    [Fact]
    public async Task PreWarm_StartingAfterAResetHasFinished_RunsAgain()
    {
        // Arrange — the reset's claim is a count released on the way out, not a latch. Spelled as a latch,
        // one reset would retire speculative work for the life of the process.
        _probe.ReleaseAll();
        await _reset.RunAsync(_config, Ct);

        // Act
        SpeculativeRunOutcome preWarm = await _runner.TryRunAsync(_config, WarmUpArguments, Ct);

        // Assert
        preWarm.ShouldBe(SpeculativeRunOutcome.Completed);
        _probe.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task ResetRacingAForegroundRunOnOnePreWarm_BothComplete()
    {
        // Arrange
        string generation = CacheHomes.PlantGenerationFor(_cacheHome, _config.SolutionPath);
        Task<SpeculativeRunOutcome> preWarm = _runner.TryRunAsync(_config, WarmUpArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);

        // Act — here the atomic exchange is raced by two different kinds of caller. Only one can win the
        // pre-warm's claim; the loser must find nothing rather than trip over a half-cleared field.
        Task<CacheResetOutcome> reset = _reset.RunAsync(_config, Ct);
        Task<ProcessResult> foreground = _runner.RunAsync(_config, ForegroundArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);
        _probe.ReleaseAll();

        // Assert — whichever order the lock admits them in, both get what they came for and the pre-warm is
        // cancelled once.
        (await reset).Dropped.ShouldBe([Path.GetFileName(generation)]);
        (await foreground).ExitCode.ShouldBe(0);
        (await preWarm).ShouldBe(SpeculativeRunOutcome.StoodDown);
        _probe.Cancelled.ShouldBe(1);
    }

    [Fact]
    public async Task PreWarmThatAlreadyFinished_IsNotCancelledRetroactivelyByAReset()
    {
        // Arrange — a pre-warm run to completion, then a reset as the later caller. A reset reclaims through
        // the same published claim a run does, so withdrawing the claim as the pass ends has to stop both.
        _probe.ReleaseAll();
        (await _runner.TryRunAsync(_config, WarmUpArguments, Ct)).ShouldBe(SpeculativeRunOutcome.Completed);
        CancellationToken preWarmToken = _probe.Tokens.ShouldHaveSingleItem();

        // Act
        await _reset.RunAsync(_config, Ct);

        // Assert
        preWarmToken.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task Reset_CancellingAPreWarm_StillWaitsForItToLetGoBeforeDeleting()
    {
        // Arrange — the safety half: yielding must shorten the wait without shortening it to nothing. The
        // abandoned pass is held in its reap, still holding the lease, which is the one stretch in which a
        // reset that had skipped the queue rather than jumping it would delete the generation out from under
        // a live jb.
        string generation = CacheHomes.PlantGenerationFor(_cacheHome, _config.SolutionPath);
        _probe.HoldReap = true;
        Task<SpeculativeRunOutcome> preWarm = _runner.TryRunAsync(_config, WarmUpArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);

        // Act
        Task<CacheResetOutcome> reset = _reset.RunAsync(_config, Ct);
        await _probe.Dying.WaitAsync(Ct);

        // Assert — a negative window, since a delete that has not happened raises no event.
        await Task.Delay(ReapWindow, Ct);
        Directory.Exists(generation).ShouldBeTrue();

        _probe.Reap();
        CacheResetOutcome outcome = await reset;
        outcome.Dropped.ShouldBe([Path.GetFileName(generation)]);
        (await preWarm).ShouldBe(SpeculativeRunOutcome.StoodDown);
    }

    /// <summary>
    ///     Stands in for <c>jb</c> as an <see cref="IProcessRunner" /> that parks each run until the test
    ///     releases it, counts the ones that were cancelled instead, and keeps the token each run was handed
    ///     so a test can ask whether it was cancelled after the fact.
    /// </summary>
    /// <remarks>
    ///     Cancellation is rethrown rather than swallowed, which is what <see cref="ProcessRunner" /> does once
    ///     it has tree-killed <c>jb</c>.
    /// </remarks>
    private sealed class YieldProbe : IProcessRunner
    {
        private readonly TaskCompletionSource _dying = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _reaped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _starts = new(0);
        private readonly List<CancellationToken> _tokens = [];
        private int _cancelled;
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public int Cancelled => Volatile.Read(ref _cancelled);

        /// <summary>
        ///     Gets or sets a value indicating whether a cancelled run is held in its reap until <see cref="Reap" />,
        ///     still holding the slot and the lease, as a killed <c>jb</c> tree holds them until
        ///     <see cref="ProcessRunner" /> has waited it out.
        /// </summary>
        /// <remarks>
        ///     Without the hold, the cancelled run unwinds inline on the canceller's own stack, so the world a test
        ///     reads afterwards is the same whether or not the canceller then waited for it.
        /// </remarks>
        public bool HoldReap { get; set; }

        /// <summary>Gets a task that completes once a cancelled run is held in its reap.</summary>
        public Task Dying => _dying.Task;

        public IReadOnlyList<CancellationToken> Tokens
        {
            get
            {
                lock (_tokens)
                {
                    return _tokens.ToList();
                }
            }
        }

        public async Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            Action<string>? onOutputLine = null)
        {
            Interlocked.Increment(ref _runs);
            lock (_tokens)
            {
                _tokens.Add(cancellationToken);
            }

            _starts.Release();

            try
            {
                await _release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelled);
                if (HoldReap)
                {
                    _dying.TrySetResult();
                    await _reaped.Task;
                }

                throw;
            }

            return JbStubs.Success;
        }

        /// <summary>Waits until one more run has started than the last time this was awaited.</summary>
        public Task WaitForNextStartAsync(CancellationToken cancellationToken)
        {
            return _starts.WaitAsync(cancellationToken);
        }

        /// <summary>Lets a run held in its reap finish dying.</summary>
        public void Reap()
        {
            _reaped.TrySetResult();
        }

        /// <summary>Lets every parked run complete, and every later one too, and lets any held reap finish.</summary>
        public void ReleaseAll()
        {
            _release.TrySetResult();
            Reap();
        }
    }
}