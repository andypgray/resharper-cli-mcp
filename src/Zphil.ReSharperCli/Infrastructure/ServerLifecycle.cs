using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Infrastructure;

/// <summary>
///     Writes the two lines that bracket a server process: what it started as, and how long it ran.
/// </summary>
/// <remarks>
///     It stands in for the Hosting lines <see cref="SerilogConfiguration.QuietedCategories" /> silences, and
///     says which configuration the process is running under rather than only that it exists. Each field
///     answers a question the log otherwise cannot, the run cap above all: an operator who has set
///     <c>RESHARPER_MCP_TIMEOUT_SECS</c> in a client config has no other way to confirm the server read it,
///     and a value the client did not pass through looks exactly like one that was ignored.
/// </remarks>
/// <param name="runTimeout">
///     The cap this process resolved, passed from the composition root rather than re-read here: the point of
///     naming it is to report the value actually in force.
/// </param>
/// <param name="logLevel">
///     The file sink's minimum level, passed from the composition root for the same reason as
///     <paramref name="runTimeout" />: <see cref="SerilogConfiguration.InitializeFileLogger" /> resolved it
///     once, and a re-parse here could disagree with the sink it claims to describe.
/// </param>
/// <param name="childLifetime">
///     Read for its guarantee alone. What stops a <c>jb</c> outliving a hard-killed server is an OS primitive
///     that shows up nowhere in a working session — only in its absence, weeks later, as a forked cache
///     generation — so the fingerprint is where a reader finds out whether the platform offered one at all.
/// </param>
internal sealed class ServerLifecycle(
    ConfigResolver configResolver,
    IEnvironment environment,
    ChildProcessLifetime childLifetime,
    TimeSpan runTimeout,
    LogEventLevel logLevel,
    ILogger<ServerLifecycle> logger) : IHostedService
{
    private readonly Stopwatch _uptime = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _uptime.Start();

        logger.LogInformation(
            "resharper-cli-mcp {Version} started: pid {ProcessId}, working directory {WorkingDirectory}, cache home {CacheHome}, "
            + "run cap {RunCap}, pre-warm {PreWarm}, orphan guard {OrphanGuard}, log level {LogLevel}, session id {SessionIdSource}",
            ServerVersion.Informational,
            Environment.ProcessId,
            environment.CurrentDirectory,
            configResolver.ResolveCacheHome(),
            // Pre-formatted, like the run-cap message a timeout reports with: a TimeSpan property renders as
            // a quoted "00:20:00" under this output template, where "20 minutes" is what a reader wants.
            DurationFormatter.Format(runTimeout),
            CacheWarmer.IsEnabled(environment.GetVariable(CacheWarmer.EnableVariable)) ? "on" : "off",
            childLifetime.Guarantee,
            logLevel,
            SerilogConfiguration.SessionIdIsClientSupplied ? "from CLAUDE_CODE_SESSION_ID" : "generated");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("resharper-cli-mcp stopping after {Uptime}", DurationFormatter.Format(_uptime.Elapsed));
        return Task.CompletedTask;
    }
}