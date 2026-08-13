using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Services;

/// <summary>
///     What <see cref="JbRunner" /> owns on behalf of both services: naming the failed subcommand in the
///     error, bounding how much of a failed run's standard error comes back with it, and stamping the warm
///     marker on every run that succeeds. Also the shape of the speculative entry point — it skips instead
///     of queueing, and reports instead of throwing — which is what lets background work never affect a
///     call the user made.
/// </summary>
public sealed class JbRunnerTests : IDisposable
{
    private static readonly TimeSpan RecentlyEnough = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private readonly ResolvedConfig _config;
    private readonly FakeEnvironment _environment = new();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly JbRunLock _runLock = new(JbRunTimeout.Default);
    private readonly JbRunner _runner;

    public JbRunnerTests()
    {
        _config = Configs.Bare("/sln/App.sln", _environment.CreateTempDirectory());
        _runner = JbRunners.Create(_processRunner, _runLock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_NamesTheSubcommandItWasGiven()
    {
        // Arrange — the subcommand comes from the argument list rather than a parameter, so it cannot drift
        // from what jb was actually asked to do.
        StubExit(3, "profile not found");

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["cleanupcode", _config.SolutionPath], Ct));

        // Assert
        exception.Message.ShouldStartWith("jb cleanupcode exited with code 3.");
        exception.Message.ShouldContain("profile not found");
    }

    [Fact]
    public async Task RunAsync_VeryLongStandardError_KeepsOnlyTheTail()
    {
        // Arrange — jb can emit megabytes on a bad run; only the end of it diagnoses anything, and the
        // response budget is not the place to find that out.
        string noise = new('x', 5000);
        StubExit(1, noise + "the actual failure");

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert
        exception.Message.ShouldContain("the actual failure");
        exception.Message.Length.ShouldBeLessThan(2500);
    }

    [Fact]
    public void StandardErrorTail_NoStandardErrorAtAll_IsEmptyRatherThanThrowing()
    {
        // Arrange & Act — a defaulted ProcessResult carries a null standard error, and the paths that quote
        // a tail (a non-zero exit, a missing SARIF file) are already reporting a failure. Adding a
        // NullReferenceException on top of one would replace the diagnosis with a crash.
        string tail = JbRunner.StandardErrorTail(null);

        // Assert
        tail.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_SucceedingRun_StampsTheWarmMarker()
    {
        // Arrange — a real call warms the cache generation just as thoroughly as a pre-warm does, so it has
        // to refresh the debounce too, or every session start would re-analyse a solution just worked on.
        StubExit(0, string.Empty);

        // Act
        await _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        JbWarmMarker.IsFreshWithin(_config.SolutionPath, _config.CacheHome, RecentlyEnough).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_FailingRun_LeavesTheWarmMarkerUnstamped()
    {
        // Arrange — a jb that exited non-zero warmed nothing worth skipping a pre-warm over.
        StubExit(2, "boom");

        // Act
        await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert
        JbWarmMarker.IsFreshWithin(_config.SolutionPath, _config.CacheHome, RecentlyEnough).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_SucceedingRun_DischargesAPrecedingCacheReset()
    {
        // Arrange — a reset promises the next run is cold, and this is that run: the cache it just built is
        // this solution's own, so the promise is kept and there is nothing left to hold anything back from.
        JbColdTombstone.Write(_config.SolutionPath, _config.CacheHome);
        StubExit(0, string.Empty);

        // Act
        await _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        JbColdTombstone.Exists(_config.SolutionPath, _config.CacheHome).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_FailingRun_LeavesTheCacheResetUndischarged()
    {
        // Arrange — a jb that exited non-zero may have built nothing, so the reset's promise still stands and
        // the next attempt must not be allowed to shortcut it.
        JbColdTombstone.Write(_config.SolutionPath, _config.CacheHome);
        StubExit(2, "boom");

        // Act
        await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert
        JbColdTombstone.Exists(_config.SolutionPath, _config.CacheHome).ShouldBeTrue();
    }

    [Fact]
    public async Task TryRunAsync_CacheGenerationFree_RunsAndStampsTheWarmMarker()
    {
        // Arrange
        StubExit(0, string.Empty);

        // Act
        var result = await _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        result.ShouldNotBeNull();
        result.Value.ExitCode.ShouldBe(0);
        JbWarmMarker.IsFreshWithin(_config.SolutionPath, _config.CacheHome, RecentlyEnough).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_ColdSolutionWithAWarmSibling_SeedsTheCacheBeforeJbStarts()
    {
        // Arrange — the ordering is the whole contract: a copy landing after jb opened the generation is worse
        // than none, so the check is made from inside the spawn rather than after the call returns.
        CacheHomes.PlantWarmDonor(_config.CacheHome, SiblingSolutionPath());
        var seededAtSpawn = false;
        StubExit(0, string.Empty, () => seededAtSpawn = Directory.Exists(SeededGenerationPath()));

        // Act
        await _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        seededAtSpawn.ShouldBeTrue();
    }

    [Fact]
    public async Task TryRunAsync_ColdSolutionWithAWarmSibling_SeedsTheCacheBeforeJbStarts()
    {
        // Arrange — the speculative entry point too: a server launched inside a worktree pre-warms that
        // worktree, and the pre-warm is the very run most likely to be the cold one worth seeding.
        CacheHomes.PlantWarmDonor(_config.CacheHome, SiblingSolutionPath());
        var seededAtSpawn = false;
        StubExit(0, string.Empty, () => seededAtSpawn = Directory.Exists(SeededGenerationPath()));

        // Act
        await _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        seededAtSpawn.ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_RunHitsTheCap_SaysWhoseCapItIsAndNamesTheLever()
    {
        // Arrange — ProcessRunner reports the mechanical fact and stops there, because it does not know
        // whose timeout it was handed. Only the runner can answer the two questions a caller is left with.
        StubTimeout();

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert — every timeout a user meets is one this server chose (an MCP client would have waited
        // far longer), so the message has to disown it, name the variable that moves it, and head off the
        // retry that does not work.
        exception.Message.ShouldStartWith("jb inspectcode timed out after 10 minutes");
        exception.Message.ShouldContain("this server's, not jb's own");
        exception.Message.ShouldContain(JbRunTimeout.Variable);
        exception.Message.ShouldContain("`files` will not help");
    }

    [Fact]
    public async Task RunAsync_RunHitsARaisedCap_ReportsThatCapExactlyAsItWasSet()
    {
        // Arrange — a raised cap that still ran out must say so with the raised number, or the advice to
        // raise it reads as though nothing happened the first time. 455 seconds and not a round number of
        // minutes on purpose: the variable is configured in seconds, so a cap must never be reported back
        // rounded to a value the user did not choose.
        JbRunner runner = JbRunners.Create(_processRunner, _runLock, TimeSpan.FromSeconds(455));
        StubTimeout();

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert
        exception.Message.ShouldStartWith("jb inspectcode timed out after 7 minutes 35 seconds");
    }

    [Fact]
    public async Task TryRunAsync_RunHitsTheCap_SkipsInsteadOfReportingAFailure()
    {
        // Arrange — the cap protects a caller who is waiting, and nobody waits on speculative work. A big
        // cold solution is both the likeliest to exceed it and the one pre-warming exists for, so treating
        // that as a fault would make the warmer log a warning for its own best-case workload.
        StubTimeout();

        // Act
        var result = await _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        result.ShouldBeNull();
        JbWarmMarker.IsFreshWithin(_config.SolutionPath, _config.CacheHome, RecentlyEnough).ShouldBeFalse();
    }

    [Fact]
    public async Task TryRunAsync_CacheGenerationAlreadyTaken_SkipsWithoutSpawningJb()
    {
        // Arrange — a lease held by someone else, standing in for a real call or another server process.
        using IDisposable? held = _runLock.TryAcquire(_config.SolutionPath, _config.CacheHome);
        held.ShouldNotBeNull();

        // Act
        var result = await _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert — not merely "did not wait": speculative work that cannot prove exclusivity never starts jb
        // at all, because a second jb on one cache generation forks a cold one.
        result.ShouldBeNull();
        await _processRunner.DidNotReceive().RunAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryRunAsync_NonZeroExit_ReportsTheResultInsteadOfThrowing()
    {
        // Arrange — background work has no channel to raise an error through, so its caller decides.
        StubExit(4, "something went wrong");

        // Act
        var result = await _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        result.ShouldNotBeNull();
        result.Value.ExitCode.ShouldBe(4);
        JbWarmMarker.IsFreshWithin(_config.SolutionPath, _config.CacheHome, RecentlyEnough).ShouldBeFalse();
    }

    [Fact]
    public async Task TryRunAsync_ItsOwnCallerCancelled_PropagatesInsteadOfReportingASkip()
    {
        // Arrange — a jb killed on the caller's own token, which is how host shutdown reaches a pre-warm.
        // The runner sees the same OperationCanceledException either way, so only the caller's token tells
        // "I was shut down" apart from "a foreground run reclaimed the cache".
        _processRunner
            .RunAsync("jb", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());
        using CancellationTokenSource callerCancelled = new();
        await callerCancelled.CancelAsync();

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(() => _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], callerCancelled.Token));
    }

    [Fact]
    public async Task TryRunAsync_AfterAForegroundRunHasFinished_RunsAgain()
    {
        // Arrange — a real call has been and gone. Retiring speculative work permanently at that point is
        // what this counter replaced: the moment it is worth most is right after a foreground run has hit
        // the cap, leaving a part-built cache and an idle user.
        StubExit(0, string.Empty);
        await _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Act
        var result = await _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        result.ShouldNotBeNull();
        result.Value.ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task TryRunAsync_ForegroundRunStillInFlight_SkipsEvenThoughItsOwnGenerationIsFree()
    {
        // Arrange — a *second* solution, so the run lock cannot be the explanation: its cache generation is
        // free throughout, and the in-flight count is the only thing left that could stop this.
        ResolvedConfig other = Configs.Bare("/sln/Other.sln", _config.CacheHome);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubBlocking(started, release);

        var foreground = _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);
        await started.Task.WaitAsync(Generous, Ct);

        // Act
        var result = await _runner.TryRunAsync(other, ["inspectcode", other.SolutionPath], Ct).WaitAsync(Generous, Ct);

        // Assert
        result.ShouldBeNull();

        release.SetResult();
        await foreground.WaitAsync(Generous, Ct);
    }

    [Fact]
    public async Task RunAsync_RunHitsTheCap_AnnouncesItWithTheConfigurationThatRanOut()
    {
        // Arrange — the config travels with the signal so a listener warms the solution that actually timed
        // out, rather than whatever the server's working directory would resolve to.
        StubTimeout();
        List<ResolvedConfig> announced = [];
        _runner.ForegroundRunTimedOut += announced.Add;

        // Act
        await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert
        announced.ShouldHaveSingleItem().SolutionPath.ShouldBe(_config.SolutionPath);
    }

    [Fact]
    public async Task RunAsync_RunsThatDidNotTimeOut_AnnounceNothing()
    {
        // Arrange — the signal means "a part-built cache and an idle user", which is true of a run killed at
        // the cap and of nothing else. A clean run left the cache warm; a failed one left a problem no
        // amount of speculative warming addresses.
        List<ResolvedConfig> announced = [];
        _runner.ForegroundRunTimedOut += announced.Add;

        // Act
        StubExit(0, string.Empty);
        await _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        StubExit(7, "boom");
        await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert
        announced.ShouldBeEmpty();
    }

    [Fact]
    public async Task TryRunAsync_RunHitsTheCap_AnnouncesNothing()
    {
        // Arrange — speculative work must never re-arm itself. A pre-warm that announced its own timeout
        // would warm, hit the cap, re-arm, and repeat for the life of the process, with only the run lock
        // pacing it. Recurrence has to advance on something the user did.
        StubTimeout();
        List<ResolvedConfig> announced = [];
        _runner.ForegroundRunTimedOut += announced.Add;

        // Act
        var result = await _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], Ct);

        // Assert
        result.ShouldBeNull();
        announced.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_RunHitsTheCap_AnnouncesOnlyOnceTheLeaseIsFreeAndTheCountIsBack()
    {
        // Arrange — both orderings pinned through behaviour rather than by reading privates. A listener that
        // can take the lease proves the lease went first; one whose own speculative run actually starts
        // proves the count was decremented first. Reverse either and nothing throws: the re-armed pass just
        // settles as a skip and the whole feature quietly buys nothing.
        StubTimeout();
        var leaseWasFree = false;
        Task<ProcessResult?>? speculative = null;

        _runner.ForegroundRunTimedOut += timedOut =>
        {
            IDisposable? lease = _runLock.TryAcquire(timedOut.SolutionPath, timedOut.CacheHome);
            leaseWasFree = lease is not null;
            lease?.Dispose();

            StubExit(0, string.Empty);
            speculative = _runner.TryRunAsync(timedOut, ["inspectcode", timedOut.SolutionPath], CancellationToken.None);
        };

        // Act
        await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert
        leaseWasFree.ShouldBeTrue();
        var reArmed = speculative.ShouldNotBeNull();
        (await reArmed.WaitAsync(Generous, Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task RunAsync_ListenerThrows_StillReportsTheTimeoutToTheCaller()
    {
        // Arrange — the signal fires while the timeout error is unwinding, so a throwing listener would
        // replace the one message telling the user whose cap it was and which variable moves it.
        StubTimeout();
        _runner.ForegroundRunTimedOut += _ => throw new InvalidOperationException("this listener is broken");

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert
        exception.Message.ShouldStartWith("jb inspectcode timed out");
        exception.Message.ShouldContain(JbRunTimeout.Variable);
    }

    [Fact]
    public async Task TryRunAsync_AlreadyCancelled_NeverStartsJbAtAll()
    {
        // Arrange — ProcessRunner calls Start() before it ever looks at its token, so without the check a
        // pre-warm cancelled in that window forks a jb only to tree-kill and reap it, holding for those
        // seconds the very lease the real call is queueing for.
        StubExit(0, string.Empty);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => _runner.TryRunAsync(_config, ["inspectcode", _config.SolutionPath], cancelled.Token));

        // Assert
        await _processRunner.DidNotReceive().RunAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A run killed at the cap, as <see cref="Zphil.ReSharperCli.Execution.ProcessRunner" /> reports it:
    ///     the mechanical message, carrying no idea of whose cap it was.
    /// </summary>
    private void StubTimeout()
    {
        _processRunner
            .RunAsync("jb", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ProcessTimeoutException("'jb' timed out."));
    }

    /// <summary>
    ///     A jb that signals when it has started and then parks until told to finish, so a test can hold a
    ///     foreground run open across an assertion.
    /// </summary>
    private void StubBlocking(TaskCompletionSource started, TaskCompletionSource release)
    {
        _processRunner
            .RunAsync("jb", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                started.TrySetResult();
                await release.Task;
                return new ProcessResult(0, string.Empty, string.Empty);
            });
    }

    private void StubExit(int exitCode, string standardError, Action? whileRunning = null)
    {
        _processRunner
            .RunAsync("jb", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                whileRunning?.Invoke();
                return new ProcessResult(exitCode, string.Empty, standardError);
            });
    }

    /// <summary>Another checkout of the same solution file, which is what makes a donor a donor.</summary>
    private string SiblingSolutionPath()
    {
        return Path.Combine(_environment.CreateTempDirectory(), Path.GetFileName(_config.SolutionPath));
    }

    /// <summary>Where a cache seeded for this runner's own solution would land.</summary>
    private string SeededGenerationPath()
    {
        return CacheHomes.GenerationPathFor(_config.CacheHome, _config.SolutionPath);
    }
}