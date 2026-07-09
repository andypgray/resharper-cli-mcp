using Microsoft.Extensions.Logging;

namespace Zphil.ReSharperCli.Tests.TestDoubles;

/// <summary>One captured logger call: its level, formatted message, exception (if any), and category name.</summary>
internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception, string Category);