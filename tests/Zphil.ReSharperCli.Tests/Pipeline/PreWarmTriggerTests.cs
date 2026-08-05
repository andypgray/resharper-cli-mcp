using NSubstitute;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Pipeline;

/// <summary>
///     The trigger, through the registration path the server really uses: connecting a client is what starts
///     the pre-warm, and nothing in a tool call is. Driven over the in-process client/server harness, which
///     composes the DI graph the way <c>Program.cs</c> does, because calling <c>Start()</c> by hand would
///     prove only that the method works and nothing about whether it is ever reached — which is exactly the
///     part that MCP's move from the <c>initialize</c> handshake to <c>server/discover</c> would otherwise
///     have broken silently. The shutdown case belongs here for the same reason: it has to run through the
///     real host stop, since that is what must not leave a <c>jb</c> behind.
/// </summary>
public sealed class PreWarmTriggerTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConnectingAClient_StartsAPreWarmOfTheDiscoveredSolution()
    {
        // Arrange & Act — the handshake is the whole trigger; nothing below touches the warmer.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(
            Ct, true, ArrangeWarmableSolution);

        // Assert
        await harness.Warmer.Finished.WaitAsync(Generous, Ct);
        harness.Warmer.Outcome.ShouldBe(WarmUpOutcome.Warmed);
        harness.Logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConnectingAClient_WithPreWarmTurnedOff_SettlesWithoutSpawningAnything()
    {
        // Arrange & Act — the harness's default, which every other test in this suite relies on: a session
        // that connects must not start speculative work behind whatever the test is about to arrange.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(
            Ct, arrange: ArrangeWarmableSolution);

        // Assert — it still settles, so this is a deterministic assertion rather than a wait for nothing.
        await harness.Warmer.Finished.WaitAsync(Generous, Ct);
        harness.Warmer.Outcome.ShouldBe(WarmUpOutcome.Disabled);
        await harness.ProcessRunner.DidNotReceive().RunAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisposeAsync_WithAPreWarmInFlight_CancelsItAndWaitsForIt()
    {
        // Arrange — a pre-warm mid-run when the client goes away. A jb that outlived this process would keep
        // ReSharper's own cache-generation lock after the OS dropped our lock file, which is the one orphan
        // the run lock cannot protect the next session from.
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        McpPipelineHarness harness = await McpPipelineHarness.StartAsync(
            Ct,
            true,
            (environment, processRunner) => ArrangeJbThatNeverFinishes(environment, processRunner, started));
        CacheWarmer warmer = harness.Warmer;
        await started.Task.WaitAsync(Generous, Ct);

        // Act — the real shutdown sequence: the client closes, the server's session ends, the host stops,
        // and the hosted services drain.
        await harness.DisposeAsync().AsTask().WaitAsync(Generous, Ct);

        // Assert — waited for, not merely signalled: StopAsync returning is the promise that jb has gone.
        warmer.Finished.IsCompleted.ShouldBeTrue();
        warmer.Outcome.ShouldBe(WarmUpOutcome.Cancelled);
    }

    /// <summary>A solution to find, a cache home to warm, and a <c>jb</c> that succeeds at everything.</summary>
    private static void ArrangeWarmableSolution(FakeEnvironment environment, IProcessRunner processRunner)
    {
        PlantSolution(environment);

        processRunner
            .RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call => IsVersionProbe(call.Arg<IReadOnlyList<string>>())
                ? new ProcessResult(0, "Version: 2026.1.2", string.Empty)
                : new ProcessResult(0, string.Empty, string.Empty));
    }

    /// <summary>The same, but the inspection parks until its token is cancelled, standing in for a cold run.</summary>
    private static void ArrangeJbThatNeverFinishes(
        FakeEnvironment environment,
        IProcessRunner processRunner,
        TaskCompletionSource started)
    {
        PlantSolution(environment);

        processRunner
            .RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (IsVersionProbe(call.Arg<IReadOnlyList<string>>()))
                    return new ProcessResult(0, "Version: 2026.1.2", string.Empty);

                var cancellationToken = call.Arg<CancellationToken>();
                started.TrySetResult();

                // Bounded so a regression that stopped cancelling fails this test instead of wedging the run.
                cancellationToken.WaitHandle.WaitOne(Generous);

                // Exactly what ProcessRunner surfaces once it has tree-killed the process on that token.
                cancellationToken.ThrowIfCancellationRequested();
                return new ProcessResult(0, string.Empty, string.Empty);
            });
    }

    private static void PlantSolution(FakeEnvironment environment)
    {
        File.WriteAllText(Path.Combine(environment.CurrentDirectory, "App.sln"), string.Empty);
        environment.SetVariable("JB_CACHE_HOME", environment.CreateTempDirectory());
    }

    private static bool IsVersionProbe(IReadOnlyList<string>? arguments)
    {
        return arguments is not null && arguments.Contains("--version");
    }
}