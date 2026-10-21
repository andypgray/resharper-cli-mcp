using Microsoft.Extensions.Logging;

namespace Zphil.ReSharperCli.Tests.TestDoubles;

/// <summary>Represents one captured logger call.</summary>
/// <remarks>
///     The structured properties of its message template and the state of every logging scope open around
///     it are what let a test assert on a log line by name (<c>{SolutionPath}</c>, <c>{CacheState}</c>,
///     <c>{RunId}</c>) rather than by matching substrings of a rendered sentence. Matching prose pins the
///     wording of a message alongside the fact it carries, so every reworded line breaks a test that was
///     never about the wording.
/// </remarks>
internal sealed record LogEntry(
    LogLevel Level,
    string Message,
    Exception? Exception,
    string Category,
    IReadOnlyList<KeyValuePair<string, object?>> Properties,
    IReadOnlyList<object> Scopes)
{
    /// <summary>Returns the value of the structured property <paramref name="name" />.</summary>
    public object? Property(string name)
    {
        foreach ((string key, object? value) in Properties)
            if (key == name)
                return value;

        return null;
    }

    /// <summary>Returns the value a scope open around this line pushed for <paramref name="name" />.</summary>
    /// <remarks>
    ///     Scopes are searched innermost first, which is how the logging providers themselves resolve a
    ///     repeated key.
    /// </remarks>
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