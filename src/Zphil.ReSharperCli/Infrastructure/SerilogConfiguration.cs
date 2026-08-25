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
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [{SessionId}] [{RunId}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

    /// <summary>Environment variable selecting the minimum log level (Serilog or Microsoft names accepted).</summary>
    internal const string LogLevelVariable = "RESHARPER_MCP_LOG_LEVEL";

    /// <summary>
    ///     The framework categories quieted to <see cref="LogEventLevel.Warning" />, matched as
    ///     <c>SourceContext</c> prefixes. Without them <c>Information</c> is roughly 95% MCP SDK and Hosting
    ///     chatter, and the handful of lines this server writes about its own caching are unfindable inside
    ///     it. Quieting them is what makes <c>Information</c> mean "something this server did" — which is
    ///     also why every line the SDK used to supply for free, the request timing above all, has a
    ///     replacement of this server's own.
    /// </summary>
    internal static readonly string[] QuietedCategories = ["ModelContextProtocol", "Microsoft.Hosting"];

    /// <summary>
    ///     Session id tagging every log line so concurrent server processes sharing the daily-rolling
    ///     file can be told apart, and — when launched by Claude Code — correlated with that session.
    /// </summary>
    private static readonly (string Id, bool FromClient) Session = ResolveSession();

    /// <summary>Absolute path to the daily-rolling log directory.</summary>
    internal static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Zphil.ReSharperCli",
        "logs");

    /// <summary>The <c>{SessionId}</c> every line carries.</summary>
    private static string SessionId => Session.Id;

    /// <summary>
    ///     Whether <see cref="SessionId" /> came from <c>CLAUDE_CODE_SESSION_ID</c> rather than being
    ///     invented here. Named in the startup line because it decides whether a log line can be traced back
    ///     to a client transcript at all.
    /// </summary>
    internal static bool SessionIdIsClientSupplied => Session.FromClient;

    /// <summary>
    ///     Creates the static <see cref="Log.Logger" /> with a daily rolling file sink. Call before any
    ///     host building so crash handlers can use it immediately. Returns the minimum level it resolved,
    ///     so the composition root can hand the startup fingerprint the level actually in force rather
    ///     than have it re-derived — a second parse could drift from the sink it claims to describe.
    /// </summary>
    public static LogEventLevel InitializeFileLogger()
    {
        // Through the seam rather than Environment.GetEnvironmentVariable, matching every other variable this
        // server reads. Instantiated rather than injected because this runs before the host exists.
        LogEventLevel minimumLevel = ParseLogLevel(new SystemEnvironment().GetVariable(LogLevelVariable));

        Log.Logger = Configure(new LoggerConfiguration(), minimumLevel)
            .WriteTo.File(
                Path.Combine(LogDirectory, "resharper-cli-mcp-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                // Concurrent server processes write to the same daily file (a shared mutex serialises
                // writes); the [{SessionId}] field then disambiguates interleaved lines.
                shared: true,
                outputTemplate: OutputTemplate)
            .CreateLogger();

        return minimumLevel;
    }

    /// <summary>
    ///     Everything about the logger that is not the sink: minimum level, the quieted framework categories,
    ///     and the enrichers behind <see cref="OutputTemplate" />'s <c>{SessionId}</c> and <c>{RunId}</c>
    ///     columns. Separated from the file sink so a test can pin the level policy against an in-memory one.
    /// </summary>
    /// <remarks>
    ///     Enricher order is load-bearing, because every one of these adds its property only if absent:
    ///     <c>FromLogContext</c> comes first so a pushed <see cref="RunIdScope" /> wins, and the fixed-width
    ///     default behind it only fills the column for lines written outside a run.
    /// </remarks>
    internal static LoggerConfiguration Configure(LoggerConfiguration configuration, LogEventLevel minimumLevel)
    {
        configuration
            .MinimumLevel.Is(minimumLevel)
            .Enrich.FromLogContext()
            .Enrich.WithProperty(RunIdScope.PropertyName, RunIdScope.OutsideARun)
            .Enrich.WithProperty("SessionId", SessionId);

        // Never below the level asked for: an override is here to quiet a category, so a server run at Error
        // must not have the frameworks alone talking at Warning.
        LogEventLevel frameworkLevel = minimumLevel > LogEventLevel.Warning ? minimumLevel : LogEventLevel.Warning;
        foreach (string category in QuietedCategories) configuration.MinimumLevel.Override(category, frameworkLevel);

        return configuration;
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
    /// <remarks>
    ///     The category filters here govern the <em>console</em> and nothing else, and that is not a
    ///     redundancy with <see cref="Configure" />'s overrides — it is the other half of the same policy.
    ///     <c>AddSerilog</c> registers a provider-scoped <c>Trace</c> rule of its own, and a rule naming a
    ///     provider outranks one naming only a category however specific it is, so a category filter added
    ///     here can never reach the file sink. Serilog's own overrides are what quiet the file; these quiet
    ///     the stderr stream the MCP client captures.
    /// </remarks>
    public static void AddSerilogLogging(this HostApplicationBuilder builder)
    {
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        foreach (string category in QuietedCategories) builder.Logging.AddFilter(category, LogLevel.Warning);

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

    private static (string Id, bool FromClient) ResolveSession()
    {
        return Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID") is { Length: > 0 } claudeSession
            ? (claudeSession, true)
            : (Guid.NewGuid().ToString("N")[..8], false);
    }
}