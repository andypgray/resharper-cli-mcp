using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Services;

/// <summary>
///     A caller the user is waiting on always wins. Pre-warming is only ever an optimisation, so a call
///     arriving while one is in flight must reclaim the cache generation rather than queue behind work
///     nobody asked for — otherwise that call would pay the queue wait <em>and</em> its own full run, which
///     is strictly worse than never pre-warming at all. The <see cref="YieldProbe" /> stands in for
///     <c>jb</c> and makes every one of these assertions an observation rather than a timing guess: it
///     signals when a run starts, blocks until the test releases it, and surfaces cancellation exactly as
///     <see cref="ProcessRunner" /> does. No sleeps.
/// </summary>
/// <remarks>
///     Both kinds of caller are driven here, against one <see cref="JbRunYield" />, because that sharing is
///     the whole fix and it is invisible from either side alone: a <see cref="JbRunner" /> and a
///     <see cref="CacheResetService" /> wired to yields of their own compile, pass every test that predates
///     this file's second half, and arbitrate against nothing. <see cref="JbRunners" /> assembles the pair
///     for the same reason the composition root does.
/// </remarks>
public sealed class JbRunYieldTests : IDisposable
{
    /// <summary>
    ///     A short wait cap, so a regression that stopped the pre-warm yielding fails these tests promptly
    ///     instead of hanging them out to the production cap. Wired to the lock's queue wait, the run
    ///     timeout, and the one place a test has to bound a wait itself.
    /// </summary>
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(10);

    private static readonly string[] WarmUpArguments = ["inspectcode", "/sln/App.sln"];
    private static readonly string[] ForegroundArguments = ["inspectcode", "/sln/App.sln", "--include=src/A.cs"];

    private readonly string _cacheHome;
    private readonly ResolvedConfig _config;
    private readonly FakeEnvironment _environment = new();
    private readonly YieldProbe _probe = new();
    private readonly CacheResetService _reset;
    private readonly JbRunner _runner;

    public JbRunYieldTests()
    {
        _cacheHome = _environment.CreateTempDirectory();
        _config = Configs.Bare("/sln/App.sln", _cacheHome);

        JbRunLock runLock = new(Cap);
        JbRunYield runYield = new();

        _runner = JbRunners.Create(_probe, runLock, runYield, Cap);
        _reset = new CacheResetService(runLock, runYield);
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
        Task<ProcessResult?> preWarm = _runner.TryRunAsync(_config, WarmUpArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);

        // Act — the real call cancels it on entry, so the lease frees up and this run gets to start.
        Task<ProcessResult> foreground = _runner.RunAsync(_config, ForegroundArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);
        _probe.ReleaseAll();

        // Assert — and the abandoned pre-warm reports a skip rather than an error: nothing went wrong.
        (await foreground).ExitCode.ShouldBe(0);
        (await preWarm).ShouldBeNull();
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
        (await _runner.TryRunAsync(_config, WarmUpArguments, Ct)).ShouldNotBeNull();
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
        // Arrange — the degenerate ordering the trigger permits: a client whose very first message is a tool
        // call, so the real run passes its cancel point before the pre-warm has anything to hand back. Left
        // alone, that call would then queue behind a pre-warm started a moment later — the one way pre-warming
        // could delay a call inside this process.
        Task<ProcessResult> foreground = _runner.RunAsync(_config, ForegroundArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);

        // Act
        ProcessResult? preWarm = await _runner.TryRunAsync(_config, WarmUpArguments, Ct);

        // Assert — it stands down rather than racing: a real run analyses the same solution into the same
        // cache generation, so while one is in flight there is nothing for a speculative run to buy.
        preWarm.ShouldBeNull();
        _probe.Runs.ShouldBe(1);

        _probe.ReleaseAll();
        (await foreground).ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task PreWarm_StartingAfterAForegroundRunHasFinished_RunsAgain()
    {
        // Arrange — the same ordering a moment later, and the answer is now the opposite one. Standing down
        // while a call is in flight is the invariant worth keeping; staying down for the rest of the process
        // was an accident of spelling that invariant as a latch, and it switched the pre-warm off precisely
        // when a call that had just hit the cap most needed it.
        _probe.ReleaseAll();
        await _runner.RunAsync(_config, ForegroundArguments, Ct);

        // Act
        ProcessResult? preWarm = await _runner.TryRunAsync(_config, WarmUpArguments, Ct);

        // Assert
        preWarm.ShouldNotBeNull();
        _probe.Runs.ShouldBe(2);
    }

    [Fact]
    public async Task TwoForegroundRunsRacingOnePreWarm_BothComplete()
    {
        // Arrange
        Task<ProcessResult?> preWarm = _runner.TryRunAsync(_config, WarmUpArguments, Ct);
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
        (await preWarm).ShouldBeNull();
        _probe.Cancelled.ShouldBe(1);
    }

    [Fact]
    public async Task Reset_ArrivingDuringAPreWarm_ReclaimsTheCacheGeneration()
    {
        // Arrange — a pre-warm holding the lease and mid-analysis. The probe is never released, so nothing
        // below can be explained by the pass finishing on its own: left to queue, this reset would wait out
        // the whole cap and then fail.
        string generation = CacheHomes.PlantGenerationFor(_cacheHome, _config.SolutionPath);
        Task<ProcessResult?> preWarm = _runner.TryRunAsync(_config, WarmUpArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);

        // Act
        CacheResetOutcome outcome = await _reset.RunAsync(_config, Ct);

        // Assert — a reset runs no jb of its own, which is exactly why the rule written into the class that
        // runs jb never covered it.
        outcome.Dropped.ShouldBe([Path.GetFileName(generation)]);
        Directory.Exists(generation).ShouldBeFalse();
        (await preWarm).ShouldBeNull();
        _probe.Cancelled.ShouldBe(1);
    }

    [Fact]
    public async Task PreWarm_StartingWhileAResetIsInFlight_NeverRunsAtAll()
    {
        // Arrange — a reset has no seam of its own to park on, so park it on a lock another session holds.
        // Its claim is raised before RunAsync's first await, so having the task in hand is proof it is up.
        FileStream otherSession = CacheHomes.HoldLockFile(_cacheHome, _config.SolutionPath);
        Task<CacheResetOutcome> reset = _reset.RunAsync(_config, Ct);

        ProcessResult? preWarm;
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
            // Released here rather than at scope exit: the reset is queued on this very lock, so awaiting it
            // while still holding the file would be a deadlock the fixture's cap would take ten seconds to
            // break.
            await otherSession.DisposeAsync();
        }

        await reset;

        // Assert — the lock cannot be the explanation, so the claim is the only thing left. Rebuilding a
        // cache while the call to drop it is in flight is the one thing a pre-warm must never do.
        preWarm.ShouldBeNull();
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
        ProcessResult? preWarm = await _runner.TryRunAsync(_config, WarmUpArguments, Ct);

        // Assert
        preWarm.ShouldNotBeNull();
        _probe.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task ResetRacingAForegroundRunOnOnePreWarm_BothComplete()
    {
        // Arrange
        string generation = CacheHomes.PlantGenerationFor(_cacheHome, _config.SolutionPath);
        Task<ProcessResult?> preWarm = _runner.TryRunAsync(_config, WarmUpArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);

        // Act — the atomic exchange is now raced by two different *kinds* of caller. Only one can win the
        // pre-warm's claim; the loser must find nothing rather than trip over a half-cleared field.
        Task<CacheResetOutcome> reset = _reset.RunAsync(_config, Ct);
        Task<ProcessResult> foreground = _runner.RunAsync(_config, ForegroundArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);
        _probe.ReleaseAll();

        // Assert — whichever order the lock admits them in, both get what they came for and the pre-warm is
        // cancelled once.
        (await reset).Dropped.ShouldBe([Path.GetFileName(generation)]);
        (await foreground).ExitCode.ShouldBe(0);
        (await preWarm).ShouldBeNull();
        _probe.Cancelled.ShouldBe(1);
    }

    [Fact]
    public async Task PreWarmThatAlreadyFinished_IsNotCancelledRetroactivelyByAReset()
    {
        // Arrange — a pre-warm run to completion. Its source is never disposed, so a caller still holding
        // the reference could cancel it after the fact; withdrawing the claim as the pass ends is what stops
        // that, and the invariant now has two kinds of caller able to break it.
        _probe.ReleaseAll();
        (await _runner.TryRunAsync(_config, WarmUpArguments, Ct)).ShouldNotBeNull();
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
        // hook fires while the abandoned pass still holds the lease, which is the one moment at which a
        // reset that had skipped the queue rather than jumping it would already have deleted the generation
        // out from under a live jb.
        string generation = CacheHomes.PlantGenerationFor(_cacheHome, _config.SolutionPath);
        var stillThereWhenCancelled = false;
        _probe.OnCancelled = () => stillThereWhenCancelled = Directory.Exists(generation);

        Task<ProcessResult?> preWarm = _runner.TryRunAsync(_config, WarmUpArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);

        // Act
        CacheResetOutcome outcome = await _reset.RunAsync(_config, Ct);

        // Assert
        stillThereWhenCancelled.ShouldBeTrue();
        outcome.Dropped.ShouldBe([Path.GetFileName(generation)]);
        (await preWarm).ShouldBeNull();
    }

    /// <summary>
    ///     An <see cref="IProcessRunner" /> that parks each run until the test releases it, counts the ones
    ///     that were cancelled instead, and keeps the token each run was handed so a test can ask whether it
    ///     was cancelled after the fact. Cancellation is rethrown rather than swallowed, which is what
    ///     <see cref="ProcessRunner" /> does once it has tree-killed <c>jb</c>.
    /// </summary>
    private sealed class YieldProbe : IProcessRunner
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _starts = new(0);
        private readonly List<CancellationToken> _tokens = [];
        private int _cancelled;
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public int Cancelled => Volatile.Read(ref _cancelled);

        /// <summary>
        ///     Invoked on the cancelled run's own stack, before it rethrows and therefore before the lease
        ///     is released — the one moment a test can observe what the world looked like while the
        ///     abandoned pass still held the cache generation.
        /// </summary>
        public Action? OnCancelled { get; set; }

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
            CancellationToken cancellationToken)
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
                OnCancelled?.Invoke();
                throw;
            }

            return new ProcessResult(0, string.Empty, string.Empty);
        }

        /// <summary>Wait until one more run has started than the last time this was awaited.</summary>
        public Task WaitForNextStartAsync(CancellationToken cancellationToken)
        {
            return _starts.WaitAsync(cancellationToken);
        }

        /// <summary>Let every parked run — and every later one — complete.</summary>
        public void ReleaseAll()
        {
            _release.TrySetResult();
        }
    }
}