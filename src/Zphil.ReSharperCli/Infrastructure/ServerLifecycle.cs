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
///     <para>
///         It stands in for the Hosting lines <see cref="SerilogConfiguration.QuietedCategories" /> silences,
///         and says which configuration the process is running under rather than only that it exists. Each
///         field answers a question the log otherwise cannot, the run cap above all: an operator who has set
///         <c>RESHARPER_MCP_TIMEOUT_SECS</c> in a client config has no other way to confirm the server read it,
///         and a value the client did not pass through looks the same as one that was ignored.
///     </para>
///     <para>
///         <paramref name="runTimeout" /> and <paramref name="logLevel" /> come from the composition root
///         rather than being re-read here, because the line exists to report the values in force, and a
///         re-parse could disagree with what <see cref="SerilogConfiguration.InitializeFileLogger" /> resolved.
///         <paramref name="childLifetime" /> is read for its guarantee alone: the OS primitive that stops a
///         <c>jb</c> outliving a hard-killed server shows up only in its absence, as a forked cache generation,
///         so this line is where a reader learns whether the platform offered one.
///     </para>
/// </remarks>
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
            EnvironmentSwitch.IsOn(environment.GetVariable(CacheWarmer.EnableVariable)) ? "on" : "off",
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