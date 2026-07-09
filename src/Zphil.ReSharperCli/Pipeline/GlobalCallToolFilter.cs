using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Zphil.ReSharperCli.Infrastructure;

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
    public static IMcpServerBuilder WithGlobalCallToolFilter(this IMcpServerBuilder builder)
    {
        return builder.WithRequestFilters(filters =>
        {
            filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
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
                    return ErrorResult(ex.Message);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The SDK's argument binder wraps a coercer-thrown UserErrorException in
                    // JsonException(s). Walk the chain so the friendly valid-values message surfaces
                    // silently, exactly as a directly-thrown UserErrorException would.
                    if (FindUserError(ex) is { } wrapped) return ErrorResult(wrapped.Message);

                    context.Server.Services?.GetService<ILoggerFactory>()
                        ?.CreateLogger(typeof(GlobalCallToolFilter))
                        .LogWarning(ex, "Tool '{ToolName}' failed", context.Params.Name);

                    return ErrorResult(ex.Message);
                }

                if (result.IsError is not true)
                {
                    int maxChars = ResponseTruncator.ComputeMaxChars(
                        context.Server.Services?.GetService<IEnvironment>()?.GetVariable("MAX_MCP_OUTPUT_TOKENS"));
                    string toolName = context.Params.Name;
                    foreach (ContentBlock contentBlock in result.Content)
                        if (contentBlock is TextContentBlock textBlock)
                            textBlock.Text = ResponseTruncator.TruncateIfNeeded(textBlock.Text, toolName, maxChars);
                }

                return result;
            });
        });
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