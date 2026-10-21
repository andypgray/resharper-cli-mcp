namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Represents a handle whose disposal runs <paramref name="release" /> once and only once, however many
///     times it is disposed.
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