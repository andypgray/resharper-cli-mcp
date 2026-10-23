using Shouldly;

namespace Zphil.ReSharperCli.Tests.TestDoubles;

/// <summary>Asserts on the lines a <see cref="CapturingLoggerProvider" /> captured.</summary>
/// <remarks>
///     <see cref="ShouldlyMethodsAttribute" /> makes a red quote the calling test's line rather than this file's,
///     which is where a reader learns which line was missing.
/// </remarks>
[ShouldlyMethods]
internal static class CapturingLoggerProviderAssertions
{
    /// <summary>
    ///     Asserts that exactly one captured line carries the structured property <paramref name="name" />, and
    ///     returns it.
    /// </summary>
    /// <remarks>
    ///     A red lists every captured line with its level and property names, because a count alone does not say
    ///     what was logged instead.
    /// </remarks>
    public static LogEntry ShouldHaveSingleLineWith(this CapturingLoggerProvider logs, string name)
    {
        IEnumerable<string> lines = logs.Entries.Select(entry =>
            $"[{entry.Level}] {entry.Message} {{{string.Join(", ", entry.Properties.Select(property => property.Key))}}}");
        string captured = string.Join("\n", lines);

        return logs.WithProperty(name).ShouldHaveSingleItem($"exactly one line carrying {{{name}}}; captured:\n{captured}");
    }
}