using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     <see cref="JbRunSlot" /> exists so one server never has two <c>jb</c> processes in flight, whichever
///     solutions they are against. A run is a whole-solution multi-core analysis, so two of them share the
///     machine rather than the work: what <see cref="JbRunLock" /> cannot see, because different solutions
///     are different cache generations and pass it uncontended, is exactly what these pin.
/// </summary>
public sealed class JbRunSlotTests
{
    private const string Subcommand = "inspectcode";
    private const string SolutionPath = "/repo/App.sln";
    private const string OtherSolutionPath = "/elsewhere/Other.sln";

    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    /// <summary>Long enough that a slot genuinely not taken shows up as a completed wait.</summary>
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
        _slot.Holder.ShouldBe(new JbRunSlotHolder(Subcommand, SolutionPath));
    }

    [Fact]
    public async Task TakeAsync_WhileHeldForAnotherSolution_WaitsForIt()
    {
        // Arrange — the whole point, and the case JbRunLock cannot see: two solutions are two cache
        // generations, so the lock lets both through and only this stops two jb processes running at once.
        IDisposable first = await _slot.TakeAsync(Subcommand, SolutionPath, Ct);

        // Act / Assert
        await ShouldAdmitTheNextCallerOnlyAfter(first);
    }

    [Fact]
    public async Task TakeAsync_AfterTheHolderReleases_NamesTheNewHolder()
    {
        // Arrange — the holder is read for a log line naming what a caller queued behind, so it has to
        // follow the handover rather than only the first taker.
        IDisposable first = await _slot.TakeAsync(Subcommand, SolutionPath, Ct);
        first.Dispose();

        // Act
        IDisposable second = await _slot.TakeAsync("cleanupcode", OtherSolutionPath, Ct);

        // Assert — and a released slot names nobody, so a wait that was never served cannot blame a run.
        _slot.Holder.ShouldBe(new JbRunSlotHolder("cleanupcode", OtherSolutionPath));
        second.Dispose();
        _slot.Holder.ShouldBeNull();
    }

    [Fact]
    public async Task TakeAsync_Cancelled_EndsTheWaitRatherThanServingItOut()
    {
        // Arrange — the wait has no cap of its own, so the client's cancellation is what ends it. Without
        // that this would be unbounded rather than merely long.
        using IDisposable held = await _slot.TakeAsync(Subcommand, SolutionPath, Ct);
        using CancellationTokenSource cancelled = new();

        // Act
        Task<IDisposable> queued = _slot.TakeAsync("cleanupcode", OtherSolutionPath, cancelled.Token);
        await cancelled.CancelAsync();

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(() => queued);
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

        // Assert — a claim that did not really take the slot would let a jb run beside the pass, which is
        // the whole failure this class exists to prevent.
        speculative.ShouldNotBeNull();
        await ShouldAdmitTheNextCallerOnlyAfter(speculative);
    }

    [Fact]
    public async Task Dispose_CalledTwice_DoesNotAdmitAnExtraRun()
    {
        // Arrange — an over-release would put two jb processes on the machine while one still holds the
        // slot, which is exactly the state this bounds.
        IDisposable first = await _slot.TakeAsync(Subcommand, SolutionPath, Ct);
        first.Dispose();
        first.Dispose();

        // Act
        IDisposable second = await _slot.TakeAsync(Subcommand, SolutionPath, Ct);

        // Assert — the next caller is still queued behind the live holder rather than let in by the double
        // release.
        await ShouldAdmitTheNextCallerOnlyAfter(second);
    }

    [Fact]
    public async Task TakeAsync_AfterANotableWait_SaysWhatItQueuedBehind()
    {
        // Arrange — the wait is the whole point: nothing else in the log records that a call spent minutes
        // behind this server's own run on another solution, and read from outside it looks like a slow run.
        CapturingLoggerProvider logs = new();
        JbRunSlot slot = JbRunners.Slot(Logs.Capturing(logs));
        IDisposable held = await slot.TakeAsync("cleanupcode", OtherSolutionPath, Ct);

        // Act — released past JbRunLock.NotableWait, the same threshold the lock judges its own waits by.
        Task<IDisposable> queued = slot.TakeAsync(Subcommand, SolutionPath, Ct);
        await Task.Delay(JbRunLock.NotableWait + HeldFor, Ct);
        held.Dispose();
        (await queued.WaitAsync(Generous, Ct)).Dispose();

        // Assert — the holder named is the run that was ahead, not the one that got in.
        LogEntry waited = logs.WithProperty("HolderSubcommand").ShouldHaveSingleItem();
        waited.Level.ShouldBe(LogLevel.Information);
        waited.Property("HolderSubcommand").ShouldBe("cleanupcode");
        waited.Property("HolderSolutionPath").ShouldBe(OtherSolutionPath);
    }

    [Fact]
    public async Task TakeAsync_Uncontended_RecordsTheAcquireWithoutClaimingAWait()
    {
        // Arrange — the ordinary case is every call, so it rides at Debug: an Information line per run for
        // a wait nobody served would bury the caching events that cost real minutes.
        CapturingLoggerProvider logs = new();
        JbRunSlot slot = JbRunners.Slot(Logs.Capturing(logs));

        // Act
        using IDisposable taken = await slot.TakeAsync(Subcommand, SolutionPath, Ct);

        // Assert
        LogEntry took = logs.WithProperty("WaitedMs").ShouldHaveSingleItem();
        took.Level.ShouldBe(LogLevel.Debug);
        logs.WithProperty("HolderSubcommand").ShouldBeEmpty();
    }

    /// <summary>
    ///     A caller the user is waiting on, queued on another solution behind <paramref name="holder" />: still
    ///     parked after <see cref="HeldFor" />, and admitted once the holder lets go.
    /// </summary>
    private async Task ShouldAdmitTheNextCallerOnlyAfter(IDisposable holder)
    {
        Task<IDisposable> queued = _slot.TakeAsync("cleanupcode", OtherSolutionPath, Ct);
        await Task.Delay(HeldFor, Ct);
        queued.IsCompleted.ShouldBeFalse();

        holder.Dispose();
        (await queued.WaitAsync(Generous, Ct)).Dispose();
    }
}