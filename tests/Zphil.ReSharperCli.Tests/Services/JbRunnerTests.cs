using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;

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

    private readonly ResolvedConfig _config;
    private readonly FakeEnvironment _environment = new();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly JbRunLock _runLock = new();
    private readonly JbRunner _runner;

    public JbRunnerTests()
    {
        _config = new ResolvedConfig("/sln/App.sln", null, null, _environment.CreateTempDirectory(), null, null, "jb");
        _runner = new JbRunner(_processRunner, _runLock);
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

    private void StubExit(int exitCode, string standardError)
    {
        _processRunner
            .RunAsync("jb", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(exitCode, string.Empty, standardError));
    }
}