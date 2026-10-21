using Microsoft.Extensions.DependencyInjection;
using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Pipeline;

/// <summary>
///     Starts the background cache pre-warm as soon as a client says anything at all.
/// </summary>
/// <remarks>
///     <para>
///         A session usually idles for minutes between connecting and its first tool call, and a cold
///         <c>jb</c> run costs minutes, so that window is the one worth spending.
///     </para>
///     <para>
///         An incoming-message filter rather than a handler for a named handshake method, because MCP
///         revisions differ in their handshake: 2025-11-25 opens with <c>initialize</c> and
///         <c>notifications/initialized</c>, while 2026-07-28 replaces both with <c>server/discover</c> and
///         rejects <c>notifications/initialized</c> outright. Triggering on any message means a new revision
///         cannot silently switch this feature off.
///     </para>
///     <para>
///         The filter costs one already-taken flag per message and returns before awaiting anything, because
///         <see cref="CacheWarmer.Start" /> only queues work. It fires at most once, and even a client whose
///         first message is a tool call loses nothing, because a caller the user is waiting on always outranks
///         the speculative run.
///     </para>
///     <para>
///         The one-shot lives here rather than in the warmer, which is re-armable: a
///         <see cref="CacheWarmer.Start" /> on every message would resolve a configuration on every message,
///         since <c>ConfigResolver</c> is uncached and <c>JbLocator</c> caches successes only, so a server in a
///         repo with no solution would silently re-probe the <c>jb</c> candidates, a probe timeout each, every
///         time.
///     </para>
/// </remarks>
internal static class PreWarmTrigger
{
    /// <summary>
    ///     Wires the pre-warm to the arrival of the session's first message.
    /// </summary>
    /// <remarks>
    ///     Resolving the warmer per message from the host's own provider, <c>context.Server.Services</c>, keeps
    ///     this a single registration with no captured instance. The flag is per registration rather than
    ///     static, so it is scoped to a host rather than to a process and cannot leak between two servers built
    ///     in one.
    /// </remarks>
    public static IMcpServerBuilder WithPreWarmTrigger(this IMcpServerBuilder builder)
    {
        var triggered = 0;

        return builder.WithMessageFilters(filters =>
        {
            filters.AddIncomingFilter(next => (context, cancellationToken) =>
            {
                if (Interlocked.Exchange(ref triggered, 1) == 0)
                    context.Server.Services?.GetService<CacheWarmer>()?.Start();

                return next(context, cancellationToken);
            });
        });
    }
}