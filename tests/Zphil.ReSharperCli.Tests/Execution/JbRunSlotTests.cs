using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     Pins that <see cref="JbRunSlot" /> never lets one server have two <c>jb</c> processes in flight,
///     whichever solutions they are against.
/// </summary>
public sealed class JbRunSlotTests
{
    private const string Subcommand = "inspectcode";
    private const string SolutionPath = "/repo/App.sln";
    private const string OtherSolutionPath = "/elsewhere/Other.sln";
    private const string EarlierSolutionPath = "/before/Earlier.sln";

    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    /// <summary>Long enough that a slot not taken shows up as a completed wait.</summary>
    private static readonly TimeSpan HeldFor = TimeSpan.FromMilliseconds(200);

    private readonly JbRunSlot _slot = JbRunners.Slot();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TakeAsync_WithNothingHoldingTheSlot_GrantsItAtOnce()
    {
        // Arrange
        var waited = Stopwatch.StartNew();

        // Act
        using IDisposable taken = await _slot.TakeAsync(Subcommand, SolutionPath, Ct);

        // Assert — the bound costs an uncontended call nothing, which is the ordinary case.
        waited.Elapsed.ShouldBeLessThan(HeldFor);
    }

    [Fact]
    public async Task TakeAsync_WhileHeldForAnotherSolution_WaitsForIt()
    {
        // Arrange — the case JbRunLock cannot see: two solutions are two cache generations, so the lock lets
        // both through and only the slot stops two jb processes running at once.
        IDisposable first = await _slot.TakeAsync(Subcommand, SolutionPath, Ct);

        // Act / Assert
        await ShouldAdmitTheNextCallerOnlyAfter(first);
    }

    [Fact]
    public async Task TakeAsync_Cancelled_EndsTheWaitRatherThanServingItOut()
    {
        // Arrange — the wait has no cap of its own, so the client's cancellation is what ends it. Without
        // that this would be unbounded, not only long.
        using IDisposable held = await _slot.TakeAsync(Subcommand, SolutionPath, Ct);
        using CancellationTokenSource cancelled = new();

        // Act
        Task<IDisposable> queued = _slot.TakeAsync("cleanupcode", OtherSolutionPath, cancelled.Token);
        await cancelled.CancelAsync();

        // Assert — bounded, so a wait the cancellation never reached reds as a timeout instead of hanging the run.
        await Should.ThrowAsync<OperationCanceledException>(() => queued.WaitAsync(Generous, Ct));
    }

    [Fact]
    public async Task TryTake_WhileHeld_SkipsWithoutWaiting()
    {
        // Arrange — speculative work skips rather than queues, and the zero wait is structural rather than
        // a short promise: were it a wait, this would hang instead of failing.
        using IDisposable held = await _slot.TakeAsync(Subcommand, SolutionPath, Ct);
        var waited = Stopwatch.StartNew();

        // Act
        IDisposable? speculative = _slot.TryTake(Subcommand, OtherSolutionPath);

        // Assert
        speculative.ShouldBeNull();
        waited.Elapsed.ShouldBeLessThan(HeldFor);
    }

    [Fact]
    public async Task TryTake_WhileFree_HoldsItAgainstACallerTheUserIsWaitingOn()
    {
        // Act
        IDisposable? speculative = _slot.TryTake(Subcommand, SolutionPath);

        // Assert — a claim that did not take the slot would let a jb run beside the pass, which is the failure
        // the slot exists to prevent.
        speculative.ShouldNotBeNull();
        await ShouldAdmitTheNextCallerOnlyAfter(speculative);
    }

    [Fact]
    public async Task Dispose_CalledTwice_DoesNotAdmitAnExtraRun()
    {
        // Arrange — an over-release would put two jb processes on the machine while one still holds the
        // slot, which is the state the slot bounds.
        IDisposable first = await _slot.TakeAsync(Subcommand, SolutionPath, Ct);
        first.Dispose();
        first.Dispose();

        // Act — bounded, so a release that stopped releasing reds as a timeout instead of hanging the run.
        IDisposable second = await _slot.TakeAsync(Subcommand, SolutionPath, Ct).WaitAsync(Generous, Ct);

        // Assert — the next caller is still queued behind the live holder rather than let in by the double
        // release.
        await ShouldAdmitTheNextCallerOnlyAfter(second);
    }

    [Fact]
    public async Task TakeAsync_AfterANotableWait_SaysWhatItQueuedBehind()
    {
        // Arrange — nothing else in the log records that a call spent minutes behind this server's own run on
        // another solution, and read from outside, such a wait looks like a slow run.
        CapturingLoggerProvider logs = new();
        JbRunSlot slot = JbRunners.Slot(Logs.Capturing(logs));

        // An earlier run takes the slot and lets go first, so the line has to follow the handover rather than
        // name the first taker.
        (await slot.TakeAsync(Subcommand, EarlierSolutionPath, Ct)).Dispose();
        IDisposable held = await slot.TakeAsync("cleanupcode", OtherSolutionPath, Ct);

        // Act
        Task<IDisposable> queued = slot.TakeAsync(Subcommand, SolutionPath, Ct);
        await Task.Delay(JbRunLock.NotableWait + HeldFor, Ct);
        held.Dispose();
        (await queued.WaitAsync(Generous, Ct)).Dispose();

        // Assert — the holder named is the run that was ahead: not an earlier one that let go, and not the one
        // that got in.
        LogEntry waited = logs.ShouldHaveSingleLineWith("HolderSubcommand");
        waited.Level.ShouldBe(LogLevel.Information);
        waited.Property("HolderSubcommand").ShouldBe("cleanupcode");
        waited.Property("HolderSolutionPath").ShouldBe(OtherSolutionPath);
    }

    [Fact]
    public async Task TakeAsync_Uncontended_RecordsTheAcquireWithoutClaimingAWait()
    {
        // Arrange — the ordinary case is every call, so it rides at Debug: an Information line per run for
        // a wait nobody served would bury the caching events that cost minutes.
        CapturingLoggerProvider logs = new();
        JbRunSlot slot = JbRunners.Slot(Logs.Capturing(logs));

        // Act
        using IDisposable taken = await slot.TakeAsync(Subcommand, SolutionPath, Ct);

        // Assert
        LogEntry took = logs.ShouldHaveSingleLineWith("WaitedMs");
        took.Level.ShouldBe(LogLevel.Debug);
        logs.WithProperty("HolderSubcommand").ShouldBeEmpty();
    }

    /// <summary>
    ///     Asserts that a caller the user is waiting on, queued on another solution behind
    ///     <paramref name="holder" />, is still parked after <see cref="HeldFor" /> and admitted once the holder
    ///     lets go.
    /// </summary>
    private async Task ShouldAdmitTheNextCallerOnlyAfter(IDisposable holder)
    {
        Task<IDisposable> queued = _slot.TakeAsync("cleanupcode", OtherSolutionPath, Ct);
        await queued.ShouldBeAdmittedOnlyAfter(holder, HeldFor, Generous, Ct);
    }
}