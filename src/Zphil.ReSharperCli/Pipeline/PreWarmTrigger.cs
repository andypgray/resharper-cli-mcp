using Microsoft.Extensions.DependencyInjection;
using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Pipeline;

/// <summary>
///     Starts the background cache pre-warm as soon as a client says anything at all. A session usually
///     idles for minutes between connecting and its first tool call, and a cold <c>jb</c> run costs minutes,
///     so that window is the one worth spending.
/// </summary>
/// <remarks>
///     <para>
///         An incoming-message filter rather than a handler for a named handshake method, because MCP has
///         more than one handshake and is gaining more: the 2025-11-25 flow is
///         <c>initialize</c> + <c>notifications/initialized</c>, while 2026-07-28 replaced both with
///         <c>server/discover</c> — and a server on the newer version <em>rejects</em>
///         <c>notifications/initialized</c> outright. Triggering on the message rather than on its method
///         means the next revision cannot silently switch this feature off.
///     </para>
///     <para>
///         The filter costs one already-taken flag per message and returns before awaiting anything, because
///         <see cref="CacheWarmer.Start" /> only queues work. It fires at most once, so even the degenerate
///         case — a client whose very first message is a tool call — costs nothing: the speculative run and
///         the real one meet at the run lock, where the real one always wins.
///     </para>
///     <para>
///         That one-shot lives here rather than in the warmer, and it has to. The warmer is re-armable, so a
///         <see cref="CacheWarmer.Start" /> reached on every message would no longer be an already-taken
///         flag: <c>ConfigResolver</c> is deliberately uncached and does a directory enumeration plus a full
///         settings parse per call, and <c>JbLocator</c> caches successes only — so a server sitting in a
///         repo with no solution would re-probe two <c>jb</c> candidates at thirty seconds each, on every
///         message, silently. Keeping the flag here holds the claim in the paragraph above true.
///     </para>
/// </remarks>
internal static class PreWarmTrigger
{
    /// <summary>
    ///     Wire the pre-warm to the arrival of the session's first message. Resolving the warmer per message
    ///     from <c>context.Server.Services</c> — the host's own provider — keeps this a single registration
    ///     with no captured instance, matching how <see cref="GlobalCallToolFilter" /> reaches its services.
    ///     The flag is per registration rather than static, so it is scoped to a host rather than to a
    ///     process and cannot leak between two servers built in one.
    /// </summary>
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