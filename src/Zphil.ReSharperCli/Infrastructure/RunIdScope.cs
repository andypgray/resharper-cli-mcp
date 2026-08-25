using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Zphil.ReSharperCli.Infrastructure;

/// <summary>
///     The <c>{RunId}</c> column: a short process-local number identifying one unit of work — one tool call,
///     or one speculative pre-warm pass — so the lines it produced can be stitched back together.
/// </summary>
/// <remarks>
///     <para>
///         It exists because the interesting lines of a slow run are written by six different classes and
///         two concurrent callers are ordinary: a pre-warm and a tool call overlap by design, and their
///         cache-state, queue-wait and run lines interleave in one file with nothing to tell them apart.
///         <c>{SessionId}</c> already separates processes, so this only has to separate work inside one.
///     </para>
///     <para>
///         A counter rather than a GUID, because it is read by a person scanning a log file. Four digits
///         keep the column fixed-width against <see cref="OutsideARun" />, and wrapping past 9999 costs
///         alignment and nothing else — a session that makes ten thousand tool calls has bigger problems.
///     </para>
///     <para>
///         Opened as a logging <em>scope</em>, so every class writing under it needs no parameter: the MEL
///         scope reaches Serilog's output template through the provider's own enricher, and every logger in
///         the process shares that provider. It therefore flows across <c>await</c> and into any task
///         started inside the scope, and — deliberately — not into one started outside it.
///     </para>
/// </remarks>
internal static class RunIdScope
{
    /// <summary>The property name the output template renders, and the one a scope must push.</summary>
    internal const string PropertyName = "RunId";

    /// <summary>
    ///     What the column reads for a line written outside any run — startup, shutdown, the framework's own
    ///     warnings. Dashes rather than blank so the column keeps its width and an empty <c>[]</c> never
    ///     appears, which is the shape the whole log used to have.
    /// </summary>
    internal const string OutsideARun = "----";

    private static int _counter;

    /// <summary>
    ///     Open a scope tagging everything logged under it — on this async flow, by any logger in the process
    ///     — with the next run id. Dispose to close it.
    /// </summary>
    public static IDisposable? Begin(ILogger logger)
    {
        return logger.BeginScope("{" + PropertyName + "}", Next());
    }

    /// <summary>The next id, formatted as the template renders it. Internal so a test can pin the width.</summary>
    internal static string Next()
    {
        return Interlocked.Increment(ref _counter).ToString("D4", CultureInfo.InvariantCulture);
    }
}