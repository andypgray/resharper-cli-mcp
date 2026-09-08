namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     A handle whose disposal runs <paramref name="release" /> once and only once, however many times it is
///     disposed. The one spelling of that guard for the three holders here — <see cref="JbRunSlot" />'s,
///     <see cref="JbRunLock" />'s and <see cref="JbRunYield" />'s — because in each a double dispose would
///     over-release: admit a second <c>jb</c>, let a second caller into a cache generation, or drop the count
///     of waited-on callers below what is in flight and let a pre-warm start behind a live call.
/// </summary>
internal sealed class ReleaseOnce(Action release) : IDisposable
{
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        release();
    }
}