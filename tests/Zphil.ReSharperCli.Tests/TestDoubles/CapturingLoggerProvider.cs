using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Zphil.ReSharperCli.Tests.TestDoubles;

/// <summary>
///     An <see cref="ILoggerProvider" /> that records every logger call into a thread-safe queue, so a test
///     can assert on what the server logged. It pins the central error contract in
///     <c>GlobalCallToolFilter</c> — a <c>UserErrorException</c> is surfaced <em>without</em> logging, while
///     any other exception is logged as exactly one warning — and, through <see cref="Entries" />, the
///     caching events the server records about itself. Registered as the host's only logging provider (all
///     built-in providers cleared), so what it holds is the server alone.
/// </summary>
/// <remarks>
///     It captures a line's structured properties and its open scopes rather than only the rendered sentence,
///     which is what makes an assertion able to name <c>{SolutionPath}</c> or <c>{RunId}</c> instead of
///     matching prose. The scope stack is <see cref="AsyncLocal{T}" />, mirroring how the real providers hold
///     theirs, so a scope opened by one class is visible to a line written by another on the same async flow —
///     the property the <c>{RunId}</c> column depends on.
/// </remarks>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private static readonly AsyncLocal<Scope?> Current = new();

    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly TaskCompletionSource<LogEntry> _firstWarning = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    ///     Completes with the first entry logged at <see cref="LogLevel.Warning" />, whenever that arrives.
    ///     The MCP SDK logs some warnings further up the unwind than anything a test can observe from inside
    ///     the server, so "the call has returned" is not "the warning has been logged"; awaiting this turns
    ///     that race into an observation, without a sleep.
    /// </summary>
    public Task<LogEntry> FirstWarning => _firstWarning.Task;

    /// <summary>Everything captured, in the order it was logged.</summary>
    public IReadOnlyList<LogEntry> Entries => _entries.ToList();

    /// <summary>The captured entries at <see cref="LogLevel.Warning" /> — the level the filter uses for unexpected failures.</summary>
    public IReadOnlyList<LogEntry> Warnings => _entries.Where(entry => entry.Level == LogLevel.Warning).ToList();

    public ILogger CreateLogger(string categoryName)
    {
        return new CapturingLogger(categoryName, _entries, _firstWarning);
    }

    public void Dispose()
    {
    }

    /// <summary>
    ///     The entries whose message template carries a property called <paramref name="name" /> — the shape
    ///     an assertion about one event takes, since it is the template that identifies the event rather than
    ///     the sentence around it.
    /// </summary>
    public IReadOnlyList<LogEntry> WithProperty(string name)
    {
        return _entries.Where(entry => entry.Properties.Any(property => property.Key == name)).ToList();
    }

    private sealed class CapturingLogger(
        string category,
        ConcurrentQueue<LogEntry> entries,
        TaskCompletionSource<LogEntry> firstWarning) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            Scope scope = new(state, Current.Value);
            Current.Value = scope;
            return scope;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            LogEntry entry = new(
                logLevel,
                formatter(state, exception),
                exception,
                category,
                Properties(state),
                OpenScopes());

            entries.Enqueue(entry);

            // Enqueued first, so a test woken by this already sees the entry through Warnings/Entries.
            if (logLevel == LogLevel.Warning) firstWarning.TrySetResult(entry);
        }

        /// <summary>
        ///     The message template's named values. Every <c>ILogger.Log</c> call the framework's own
        ///     extension methods make passes a <c>FormattedLogValues</c>, which is exactly this list; anything
        ///     else — a bare object state — carries no names and is reported as carrying none.
        /// </summary>
        private static IReadOnlyList<KeyValuePair<string, object?>> Properties<TState>(TState state)
        {
            return state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
        }

        /// <summary>The state of every scope open on this async flow, innermost first.</summary>
        private static IReadOnlyList<object> OpenScopes()
        {
            List<object> states = [];
            for (Scope? scope = Current.Value; scope is not null; scope = scope.Parent) states.Add(scope.State);

            return states;
        }
    }

    /// <summary>
    ///     One open scope and its parent. Disposal restores the parent rather than clearing the slot, so
    ///     nested scopes unwind correctly and a scope disposed out of order cannot orphan the ones inside it.
    /// </summary>
    private sealed class Scope(object state, Scope? parent) : IDisposable
    {
        public object State { get; } = state;

        public Scope? Parent { get; } = parent;

        public void Dispose()
        {
            Current.Value = Parent;
        }
    }
}