using Shouldly;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>Assertions over a queued acquire of a run lock or run slot.</summary>
internal static class LeaseAssertions
{
    /// <summary>
    ///     Asserts that <paramref name="queued" /> is still waiting after <paramref name="heldFor" />, then that it is
    ///     admitted within <paramref name="patience" /> once <paramref name="holder" /> lets go.
    /// </summary>
    /// <remarks>
    ///     The holder is released before the first assertion, so a red does not leave it held for the rest of the
    ///     run.
    /// </remarks>
    public static async Task ShouldBeAdmittedOnlyAfter(
        this Task<IDisposable> queued, IDisposable holder, TimeSpan heldFor, TimeSpan patience, CancellationToken cancellationToken)
    {
        await Task.Delay(heldFor, cancellationToken);
        bool admittedWhileHeld = queued.IsCompleted;
        holder.Dispose();

        admittedWhileHeld.ShouldBeFalse($"The queued acquire was let in within {heldFor} while the holder still held it.");
        (await queued.WaitAsync(patience, cancellationToken)).Dispose();
    }
}