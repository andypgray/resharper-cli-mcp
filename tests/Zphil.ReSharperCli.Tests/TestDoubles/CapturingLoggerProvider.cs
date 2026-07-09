using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Zphil.ReSharperCli.Tests.TestDoubles;

/// <summary>
///     An <see cref="ILoggerProvider" /> that records every logger call into a thread-safe queue, so a test
///     can assert on what the server logged. It exists to pin the central error contract in
///     <c>GlobalCallToolFilter</c>: a <c>UserErrorException</c> is surfaced <em>without</em> logging, while
///     any other exception is logged as exactly one warning. Registered as the host's only logging provider
///     (all built-in providers cleared), so <see cref="Warnings" /> reflects the filter alone.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    /// <summary>The captured entries at <see cref="LogLevel.Warning" /> — the level the filter uses for unexpected failures.</summary>
    public IReadOnlyList<LogEntry> Warnings => _entries.Where(entry => entry.Level == LogLevel.Warning).ToList();

    public ILogger CreateLogger(string categoryName)
    {
        return new CapturingLogger(categoryName, _entries);
    }

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
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
            entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception, category));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}