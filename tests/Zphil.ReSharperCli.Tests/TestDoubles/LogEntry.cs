using Microsoft.Extensions.Logging;

namespace Zphil.ReSharperCli.Tests.TestDoubles;

/// <summary>
///     One captured logger call: its level, formatted message, exception (if any), category name, the
///     structured properties of its message template, and the state of every logging scope open around it.
/// </summary>
/// <remarks>
///     The last two are what let a test assert on a log line by <em>name</em> — <c>{SolutionPath}</c>,
///     <c>{CacheState}</c>, <c>{RunId}</c> — rather than by matching substrings of a rendered sentence.
///     Matching prose pins the wording of a message alongside the fact it carries, so every reworded line
///     breaks a test that was never about the wording.
/// </remarks>
internal sealed record LogEntry(
    LogLevel Level,
    string Message,
    Exception? Exception,
    string Category,
    IReadOnlyList<KeyValuePair<string, object?>> Properties,
    IReadOnlyList<object> Scopes)
{
    /// <summary>
    ///     The value of the structured property <paramref name="name" />, or <see langword="null" /> when this
    ///     line carries none by that name.
    /// </summary>
    public object? Property(string name)
    {
        foreach ((string key, object? value) in Properties)
            if (key == name)
                return value;

        return null;
    }

    /// <summary>
    ///     The value <paramref name="name" /> was pushed with by a scope open around this line, or
    ///     <see langword="null" /> when no scope carried one. Scopes are searched innermost first, which is how
    ///     the logging providers themselves resolve a repeated key.
    /// </summary>
    public object? ScopeValue(string name)
    {
        foreach (object scope in Scopes)
        {
            if (scope is not IEnumerable<KeyValuePair<string, object?>> properties) continue;

            foreach ((string key, object? value) in properties)
                if (key == name)
                    return value;
        }

        return null;
    }
}