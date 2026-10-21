namespace Zphil.ReSharperCli.Tests.TestDoubles;

/// <summary>
///     Records whatever a sink is handed, thread-safely, with a bounded wait for "at least this much has
///     arrived".
/// </summary>
/// <remarks>
///     A base class, so the poll loop and its give-up policy have one spelling rather than drifting apart
///     when hand-rolled per test class.
/// </remarks>
/// <param name="patience">
///     How long a wait may go unanswered before it fails: long enough that only a hang reaches it, short
///     enough to fail rather than wedge the run.
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

    /// <summary>Gets a snapshot of everything recorded so far, in arrival order.</summary>
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

    /// <summary>Waits until at least <paramref name="count" /> items have landed.</summary>
    public Task WaitForAsync(int count, CancellationToken cancellationToken)
    {
        return WaitUntilAsync(() => Count >= count, $"at least {count} recorded item(s)", cancellationToken);
    }

    /// <summary>Waits until <paramref name="condition" /> holds.</summary>
    /// <remarks>
    ///     When it still does not hold after the patience, throws a <see cref="TimeoutException" /> naming
    ///     <paramref name="awaited" /> rather than a bare cancellation.
    /// </remarks>
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