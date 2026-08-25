using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Zphil.ReSharperCli.Infrastructure;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Pipeline;

/// <summary>
///     The single point where tool-call exceptions become error results and successful responses are
///     truncated. Tool methods never <c>try/catch</c>: they throw, and this filter shapes the outcome.
/// </summary>
internal static class GlobalCallToolFilter
{
    // The SDK's argument-marshalling layer wraps a coercer-thrown UserErrorException one or two
    // JsonExceptions deep; 8 is loose headroom against a pathological chain.
    private const int MaxExceptionChainDepth = 8;

    /// <summary>
    ///     Wraps every <c>tools/call</c> so that a <see cref="UserErrorException" /> is returned to the
    ///     client as an <see cref="CallToolResult.IsError" /> result <em>without</em> logging (it is
    ///     expected, not a bug), any other exception is logged as a warning before being surfaced, and
    ///     successful text is passed through <see cref="ResponseTruncator" />. Before dispatch it also
    ///     runs <see cref="UnknownParameterGuard" /> so a hallucinated argument key becomes an actionable
    ///     error rather than a silently-dropped argument.
    /// </summary>
    /// <remarks>
    ///     It is also where a call's <see cref="RunIdScope" /> opens, because this is the outermost frame that
    ///     knows a call has begun. Everything the call goes on to cause — the config resolution, the queue
    ///     wait, the cache state, the <c>jb</c> run — is tagged with that id, which is what separates a call's
    ///     lines from a concurrent pre-warm's in one shared log file.
    /// </remarks>
    public static IMcpServerBuilder WithGlobalCallToolFilter(this IMcpServerBuilder builder)
    {
        return builder.WithRequestFilters(filters =>
        {
            filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                ILogger? logger = LoggerFor(context);
                using IDisposable? runScope = logger is null ? null : RunIdScope.Begin(logger);
                var elapsed = Stopwatch.StartNew();

                // Guarded because Describe builds its string eagerly, on every call, at a level the default
                // configuration drops.
                if (logger?.IsEnabled(LogLevel.Debug) == true)
                    logger.LogDebug(
                        "Tool {ToolName} called with {Arguments}",
                        context.Params.Name,
                        Describe(context.Params.Arguments));

                CallToolResult result;
                try
                {
                    // Reject unknown argument keys before binding; its message is a UserErrorException,
                    // so it flows through the silent-user-error path below.
                    if (UnknownParameterGuard.Validate(context.Params.Name, context.Params.Arguments) is { } unknownParameterError)
                        throw new UserErrorException(unknownParameterError);

                    result = await next(context, cancellationToken);
                }
                catch (UserErrorException ex)
                {
                    // Expected user-facing error (bad input, missing solution, a failed jb run): surface
                    // the message, don't log it — the file log is reserved for unexpected crashes.
                    ReportCompletion(logger, context.Params.Name, elapsed, "a reported error");
                    return ErrorResult(ex.Message);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The SDK's argument binder wraps a coercer-thrown UserErrorException in
                    // JsonException(s). Walk the chain so the friendly valid-values message surfaces
                    // silently, exactly as a directly-thrown UserErrorException would.
                    if (FindUserError(ex) is { } wrapped)
                    {
                        ReportCompletion(logger, context.Params.Name, elapsed, "a reported error");
                        return ErrorResult(wrapped.Message);
                    }

                    logger?.LogWarning(ex, "Tool '{ToolName}' failed", context.Params.Name);

                    ReportCompletion(logger, context.Params.Name, elapsed, "an unexpected failure");
                    return ErrorResult(ex.Message);
                }

                if (result.IsError is not true)
                {
                    int maxChars = ResponseTruncator.ComputeMaxChars(context.Server.Services?.GetService<IEnvironment>());
                    string hint = ResharperTools.TruncationHintFor(context.Params.Name);
                    foreach (ContentBlock contentBlock in result.Content)
                        if (contentBlock is TextContentBlock textBlock)
                        {
                            int before = textBlock.Text.Length;
                            textBlock.Text = ResponseTruncator.TruncateIfNeeded(textBlock.Text, hint, maxChars);

                            ReportShaping(logger, before, textBlock.Text.Length, maxChars);
                        }
                }

                ReportCompletion(logger, context.Params.Name, elapsed, result.IsError is true ? "an error result" : "a result");
                return result;
            });
        });
    }

    /// <summary>
    ///     The logger this filter writes through, or <see langword="null" /> when the host has no logging at
    ///     all. Resolved per call from the request's own provider — the same route this filter has always used
    ///     to reach services, and the only one available to a filter registered as a delegate.
    /// </summary>
    private static ILogger? LoggerFor(RequestContext<CallToolRequestParams> context)
    {
        return context.Server.Services?.GetService<ILoggerFactory>()?.CreateLogger(typeof(GlobalCallToolFilter));
    }

    /// <summary>
    ///     Close the envelope: which tool, how it ended, and how long the whole call took. At <c>Debug</c>, as
    ///     the level policy has it — the caching events inside the call are what <c>Information</c> is for. It
    ///     nonetheless replaces something real, the SDK's own <c>request handler completed in Nms</c> line,
    ///     which was the only timing the log carried before the frameworks were quieted.
    /// </summary>
    private static void ReportCompletion(ILogger? logger, string toolName, Stopwatch elapsed, string outcome)
    {
        logger?.LogDebug(
            "Tool {ToolName} returned {Outcome} after {ElapsedMs} ms", toolName, outcome, elapsed.ElapsedMilliseconds);
    }

    /// <summary>
    ///     Whether the last-resort truncator actually bit, and by how much. Silent when it did not: the
    ///     rendering ladder having already fitted the response is the ordinary case, and the interesting
    ///     event is the one where even <c>Minimal</c> overflowed.
    /// </summary>
    private static void ReportShaping(ILogger? logger, int before, int after, int maxChars)
    {
        if (before == after) return;

        logger?.LogDebug(
            "Truncated the response from {BeforeChars} to {AfterChars} characters against a {MaxChars} character budget",
            before,
            after,
            maxChars);
    }

    /// <summary>
    ///     The shape of the arguments a call arrived with, without their values: which keys, and how many
    ///     entries in each array. String content is never printed, whatever its key — this line runs ahead of
    ///     binding, so every value is unvalidated caller input and any of them can carry a path. Vouching for
    ///     kinds rather than key names is what keeps "no caller path in the log" true for parameters this
    ///     filter has never heard of. What a call resolved to is written by the frames that resolved it:
    ///     the solution by <c>ConfigResolver</c>, the severity and profile by the <c>jb</c> command line, at
    ///     this same level.
    /// </summary>
    private static string Describe(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null or { Count: 0 }) return "no arguments";

        List<string> parts = [];
        foreach ((string key, JsonElement value) in arguments)
            parts.Add(value.ValueKind == JsonValueKind.Array
                ? $"{key}=[{value.GetArrayLength()} entries]"
                : $"{key}={Scalar(value)}");

        return string.Join(", ", parts);
    }

    /// <summary>One scalar argument as the envelope reports it: its presence, or its non-string kind.</summary>
    private static string Scalar(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return "given";

        return value.ValueKind == JsonValueKind.Null ? "null" : value.ValueKind.ToString();
    }

    private static CallToolResult ErrorResult(string message)
    {
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = message }],
            IsError = true
        };
    }

    /// <summary>
    ///     Walks up to <see cref="MaxExceptionChainDepth" /> inner exceptions looking for a
    ///     <see cref="UserErrorException" /> the SDK's argument binder buried inside
    ///     <c>JsonException</c>(s), returning it (so its friendly message can surface) or
    ///     <see langword="null" /> when the failure is a genuine unexpected error.
    /// </summary>
    private static UserErrorException? FindUserError(Exception? ex)
    {
        for (var depth = 0; ex is not null && depth < MaxExceptionChainDepth; depth++)
        {
            if (ex is UserErrorException user) return user;
            ex = ex.InnerException;
        }

        return null;
    }
}