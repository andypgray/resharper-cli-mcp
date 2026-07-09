using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Zphil.ReSharperCli.Infrastructure;

/// <summary>
///     Configures Serilog file logging for post-mortem debugging of catastrophic crashes that can't
///     reach the MCP client. Logs to <c>%LOCALAPPDATA%/Zphil.ReSharperCli/logs/</c>. Nothing is written
///     to stdout — that channel is reserved for MCP JSON-RPC.
/// </summary>
internal static class SerilogConfiguration
{
    private const string OutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [{SessionId}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

    /// <summary>Environment variable selecting the minimum log level (Serilog or Microsoft names accepted).</summary>
    internal const string LogLevelVariable = "RESHARPER_MCP_LOG_LEVEL";

    /// <summary>
    ///     Session id tagging every log line so concurrent server processes sharing the daily-rolling
    ///     file can be told apart, and — when launched by Claude Code — correlated with that session.
    /// </summary>
    private static readonly string SessionId =
        Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID") is { Length: > 0 } claudeSession
            ? claudeSession
            : Guid.NewGuid().ToString("N")[..8];

    /// <summary>Absolute path to the daily-rolling log directory.</summary>
    internal static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Zphil.ReSharperCli",
        "logs");

    /// <summary>
    ///     Creates the static <see cref="Log.Logger" /> with a daily rolling file sink. Call before any
    ///     host building so crash handlers can use it immediately.
    /// </summary>
    public static void InitializeFileLogger()
    {
        LogEventLevel minimumLevel = ParseLogLevel(Environment.GetEnvironmentVariable(LogLevelVariable));

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Enrich.WithProperty("SessionId", SessionId)
            .WriteTo.File(
                Path.Combine(LogDirectory, "resharper-cli-mcp-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                // Concurrent server processes write to the same daily file (a shared mutex serialises
                // writes); the [{SessionId}] field then disambiguates interleaved lines.
                shared: true,
                outputTemplate: OutputTemplate)
            .CreateLogger();
    }

    /// <summary>Registers process-level crash handlers that log fatal errors and flush before exit.</summary>
    public static void RegisterCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Fatal(ex, "Unhandled exception");

            Log.CloseAndFlush();
        };

        // Non-fatal: log but don't close the logger — the process continues running.
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Error(e.Exception, "Unobserved task exception");
    }

    /// <summary>
    ///     Adds Serilog and console (stderr) logging to the host builder. Console goes to stderr because
    ///     stdout is reserved for the MCP JSON-RPC protocol.
    /// </summary>
    public static void AddSerilogLogging(this HostApplicationBuilder builder)
    {
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSerilog();
    }

    /// <summary>
    ///     Parses a <see cref="LogLevelVariable" /> value into a Serilog level, accepting both
    ///     <see cref="LogLevel" /> and <see cref="LogEventLevel" /> names and falling back to
    ///     <see cref="LogEventLevel.Warning" /> for null, blank, or unrecognised input.
    /// </summary>
    internal static LogEventLevel ParseLogLevel(string? envValue)
    {
        if (string.IsNullOrWhiteSpace(envValue)) return LogEventLevel.Warning;

        // Accept Microsoft.Extensions.Logging.LogLevel names. Enum.TryParse also binds numeric strings
        // ("99") to an undefined enum value, so guard with Enum.IsDefined to keep them on the fallback.
        if (Enum.TryParse(envValue, true, out LogLevel msLevel) && Enum.IsDefined(msLevel)) return LevelConvert.ToSerilogLevel(msLevel);

        // Also accept Serilog level names directly.
        if (Enum.TryParse(envValue, true, out LogEventLevel serilogLevel) && Enum.IsDefined(serilogLevel)) return serilogLevel;

        return LogEventLevel.Warning;
    }
}