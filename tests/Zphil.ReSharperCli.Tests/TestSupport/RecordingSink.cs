namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     A thread-safe recorder for whatever a sink is handed, with a bounded wait for "at least this much
///     has arrived". The base of every progress-shaped test double, so the poll loop and its give-up
///     policy — the parts that drift when hand-rolled per test class — have one spelling.
/// </summary>
/// <param name="patience">
///     How long a wait may go unanswered before it fails. Long enough that only a genuine hang reaches it,
///     short enough to fail rather than wedge the run.
/// </param>
internal class RecordingSink<T>(TimeSpan patience)
{
    private readonly Lock _gate = new();
    private readonly List<T> _items = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>A snapshot of everything recorded so far, in arrival order.</summary>
    public IReadOnlyList<T> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.ToList();
            }
        }
    }

    public void Record(T item)
    {
        lock (_gate)
        {
            _items.Add(item);
        }
    }

    /// <summary>Wait until at least <paramref name="count" /> items have landed.</summary>
    public Task WaitForAsync(int count, CancellationToken cancellationToken)
    {
        return WaitUntilAsync(() => Count >= count, $"at least {count} recorded item(s)", cancellationToken);
    }

    /// <summary>
    ///     Wait until <paramref name="condition" /> holds, failing with a message naming
    ///     <paramref name="awaited" /> rather than with a bare cancellation when it never does.
    /// </summary>
    public async Task WaitUntilAsync(Func<bool> condition, string awaited, CancellationToken cancellationToken)
    {
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        giveUp.CancelAfter(patience);

        while (!condition())
            try
            {
                await Task.Delay(5, giveUp.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Still waiting for {awaited} after {patience}, with {Count} item(s) recorded.");
            }
    }
}