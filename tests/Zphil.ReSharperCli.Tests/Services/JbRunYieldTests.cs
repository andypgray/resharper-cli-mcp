using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Services;

/// <summary>
///     A foreground call always wins. Pre-warming is only ever an optimisation, so a real call arriving
///     while one is in flight must reclaim the cache generation rather than queue behind work nobody asked
///     for — otherwise that call would pay the queue wait <em>and</em> its own full run, which is strictly
///     worse than never pre-warming at all. The <see cref="YieldProbe" /> stands in for <c>jb</c> and makes
///     every one of these assertions an observation rather than a timing guess: it signals when a run
///     starts, blocks until the test releases it, and surfaces cancellation exactly as
///     <see cref="ProcessRunner" /> does. No sleeps.
/// </summary>
public sealed class JbRunYieldTests : IDisposable
{
    private static readonly string[] WarmUpArguments = ["inspectcode", "/sln/App.sln"];
    private static readonly string[] ForegroundArguments = ["inspectcode", "/sln/App.sln", "--include=src/A.cs"];

    private readonly ResolvedConfig _config;
    private readonly FakeEnvironment _environment = new();
    private readonly YieldProbe _probe = new();
    private readonly JbRunner _runner;

    public JbRunYieldTests()
    {
        _config = Configs.Bare("/sln/App.sln", _environment.CreateTempDirectory());

        // A short wait cap so a regression that stopped the pre-warm yielding fails these tests promptly
        // instead of hanging them out to the production cap.
        _runner = JbRunners.Create(_probe, TimeSpan.FromSeconds(10));
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
        var preWarm = _runner.TryRunAsync(_config, WarmUpArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);

        // Act — the real call cancels it on entry, so the lease frees up and this run gets to start.
        var foreground = _runner.RunAsync(_config, ForegroundArguments, Ct);
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
        var foreground = _runner.RunAsync(_config, ForegroundArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);

        // Act
        var preWarm = await _runner.TryRunAsync(_config, WarmUpArguments, Ct);

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
        var preWarm = await _runner.TryRunAsync(_config, WarmUpArguments, Ct);

        // Assert
        preWarm.ShouldNotBeNull();
        _probe.Runs.ShouldBe(2);
    }

    [Fact]
    public async Task TwoForegroundRunsRacingOnePreWarm_BothComplete()
    {
        // Arrange
        var preWarm = _runner.TryRunAsync(_config, WarmUpArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);

        // Act — only one of the two can win the pre-warm's token source; the loser must find nothing rather
        // than trip over a half-cleared field, and both must still get their runs.
        var first = _runner.RunAsync(_config, ForegroundArguments, Ct);
        var second = _runner.RunAsync(_config, ForegroundArguments, Ct);
        await _probe.WaitForNextStartAsync(Ct);
        _probe.ReleaseAll();

        // Assert
        (await first).ExitCode.ShouldBe(0);
        (await second).ExitCode.ShouldBe(0);
        (await preWarm).ShouldBeNull();
        _probe.Cancelled.ShouldBe(1);
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