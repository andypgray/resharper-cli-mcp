using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     <see cref="JbRunYield" /> driven directly, with no runner, slot or lock around it. Through the runner a
///     foreground caller that has counted itself in also holds the run slot, so a refused pre-warm there is
///     explained twice over and would still be refused with this type deleted; only here is the count the one
///     thing that can say no.
/// </summary>
public sealed class JbRunYieldTests
{
    private readonly JbRunYield _yield = JbRunners.Yield();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TryEnterSpeculative_NobodyWaiting_ClaimsTheGeneration()
    {
        // Act
        using JbRunYield.SpeculativeRun? claim = _yield.TryEnterSpeculative(Ct);

        // Assert
        claim.ShouldNotBeNull();
        claim.Token.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public void TryEnterSpeculative_WhileAForegroundCallerIsIn_RefusesToStart()
    {
        // Arrange
        using IDisposable foreground = _yield.EnterForeground();

        // Act
        JbRunYield.SpeculativeRun? claim = _yield.TryEnterSpeculative(Ct);

        // Assert
        claim.ShouldBeNull();
    }

    [Fact]
    public void TryEnterSpeculative_AfterTheForegroundCallerStoodDown_ClaimsAgain()
    {
        // Arrange — a count, not a latch: a latch never cleared would retire speculative work for the life of
        // the process at exactly the moment it is worth most, right after a call hit the cap.
        _yield.EnterForeground().Dispose();

        // Act
        using JbRunYield.SpeculativeRun? claim = _yield.TryEnterSpeculative(Ct);

        // Assert
        claim.ShouldNotBeNull();
    }

    [Fact]
    public void TryEnterSpeculative_OneOfTwoOverlappingCallersStoodDown_StillRefuses()
    {
        // Arrange — "the first one returned" is not "nobody is waiting", which is what a latch cleared on the way
        // out would have got wrong.
        IDisposable first = _yield.EnterForeground();
        using IDisposable second = _yield.EnterForeground();
        first.Dispose();

        // Act
        JbRunYield.SpeculativeRun? claim = _yield.TryEnterSpeculative(Ct);

        // Assert
        claim.ShouldBeNull();
    }

    [Fact]
    public void EnterForeground_DisposedTwice_DoesNotStandDownACallerStillIn()
    {
        // Arrange — a double dispose that decremented twice would take the count to zero with a call still
        // in flight, and let a pre-warm start behind it.
        IDisposable first = _yield.EnterForeground();
        using IDisposable second = _yield.EnterForeground();

        // Act
        first.Dispose();
        first.Dispose();

        // Assert
        _yield.TryEnterSpeculative(Ct).ShouldBeNull();
    }

    [Fact]
    public void EnterForeground_WithAPassInFlight_CancelsItsToken()
    {
        // Arrange
        using JbRunYield.SpeculativeRun? claim = _yield.TryEnterSpeculative(Ct);
        claim.ShouldNotBeNull();

        // Act
        using IDisposable foreground = _yield.EnterForeground();

        // Assert — cancelled out of the way rather than waited out.
        claim.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void EnterForeground_AfterThePassWithdrewItsClaim_CancelsNothing()
    {
        // Arrange
        JbRunYield.SpeculativeRun? claim = _yield.TryEnterSpeculative(Ct);
        claim.ShouldNotBeNull();
        claim.Dispose();

        // Act
        using IDisposable foreground = _yield.EnterForeground();

        // Assert
        claim.Token.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public void SpeculativeRun_FinishedPassWithdrawing_LeavesItsSuccessorReclaimable()
    {
        // Arrange — the withdrawal is a compare-and-swap on its own claim: a finished pass must not clear a
        // later one, or a call arriving next would find nothing to cancel and queue behind it.
        JbRunYield.SpeculativeRun? finished = _yield.TryEnterSpeculative(Ct);
        using JbRunYield.SpeculativeRun? successor = _yield.TryEnterSpeculative(Ct);
        finished.ShouldNotBeNull();
        successor.ShouldNotBeNull();
        finished.Dispose();

        // Act
        using IDisposable foreground = _yield.EnterForeground();

        // Assert
        successor.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void SpeculativeRun_CallerCancels_ReachesTheClaimsToken()
    {
        // Arrange — the claim's token is linked to the caller's, so a server shutting down stops the pass too.
        using CancellationTokenSource caller = new();
        using JbRunYield.SpeculativeRun? claim = _yield.TryEnterSpeculative(caller.Token);
        claim.ShouldNotBeNull();

        // Act
        caller.Cancel();

        // Assert
        claim.Token.IsCancellationRequested.ShouldBeTrue();
    }
}