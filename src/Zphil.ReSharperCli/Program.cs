using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Serilog.Events;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Infrastructure;
using Zphil.ReSharperCli.Pipeline;
using Zphil.ReSharperCli.Prompts;
using Zphil.ReSharperCli.Resources;
using Zphil.ReSharperCli.Services;

if (args.Contains("--version"))
{
    Console.WriteLine(ServerVersion.Informational);
    return;
}

if (!Console.IsInputRedirected)
{
    // A human ran the tool at a terminal: don't hang on a silent stdio server.
    Console.WriteLine("resharper-cli-mcp is an MCP stdio server; it is started by an MCP client, not interactively.");
    Console.WriteLine("Add it to your client config with command \"resharper-cli-mcp\", or see https://github.com/andypgray/resharper-cli-mcp.");
    return;
}

// A real MCP client launched us over piped stdio. Bring up the file logger and crash handlers
// before host building so a catastrophic startup failure still lands in the post-mortem log. The
// resolved level is kept for the startup fingerprint, which must report the sink's actual level.
LogEventLevel logLevel = SerilogConfiguration.InitializeFileLogger();
SerilogConfiguration.RegisterCrashHandlers();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.AddSerilogLogging();

// Read here rather than in either consumer, and passed to both: a call is bounded by its queue wait plus
// its own run, and those two caps only mean anything together if they are the same number.
TimeSpan runTimeout = JbRunTimeout.Resolve(Environment.GetEnvironmentVariable(JbRunTimeout.Variable));

// The two fakeable seams plus the pure/concrete graph composed over them — all singletons.
builder.Services.AddSingleton<IEnvironment, SystemEnvironment>();
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<JbLocator>();
builder.Services.AddSingleton<ConfigResolver>();
builder.Services.AddSingleton(provider => new JbRunLock(
    runTimeout, provider.GetRequiredService<ILogger<JbRunLock>>()));

// Shared on purpose, like the lock and for the same reason: the lock decides who waits, the yield decides
// who is made to wait, and a second instance of either would arbitrate against nothing.
builder.Services.AddSingleton<JbRunYield>();

builder.Services.AddSingleton<CacheTransplanter>();

// By factory rather than by type: the run timeout is a value this composition root resolved above, not a
// service the container can supply.
builder.Services.AddSingleton(provider => new JbRunner(
    provider.GetRequiredService<IProcessRunner>(),
    provider.GetRequiredService<JbRunLock>(),
    provider.GetRequiredService<JbRunYield>(),
    provider.GetRequiredService<CacheTransplanter>(),
    runTimeout,
    provider.GetRequiredService<ILogger<JbRunner>>()));
builder.Services.AddSingleton<InspectService>();
builder.Services.AddSingleton<CleanupService>();
builder.Services.AddSingleton<CacheResetService>();

// First hosted service registered, so its fingerprint is the session's first line and — services stopping in
// reverse — its uptime line is the last, written after the warmer below has reported whatever it drained.
builder.Services.AddHostedService(provider => new ServerLifecycle(
    provider.GetRequiredService<ConfigResolver>(),
    provider.GetRequiredService<IEnvironment>(),
    runTimeout,
    logLevel,
    provider.GetRequiredService<ILogger<ServerLifecycle>>()));

// The factory overload is required: the generic AddHostedService<T> would build a *second* CacheWarmer, and
// the one whose run has to be drained at shutdown is the one the notification handler below started.
builder.Services.AddSingleton<CacheWarmer>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<CacheWarmer>());

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInstructions = ServerInstructions.Text;
        options.ServerInfo = new Implementation
        {
            Name = "resharper-cli-mcp",
            Title = "ReSharper CLI Tools (unofficial)",
            Version = ServerVersion.SemVer
        };
    })
    .WithStdioServerTransport()
    .WithCoercingTools()
    .WithPrompts<ResharperPrompts>()
    .WithResources<ResharperResources>()
    .WithGlobalCallToolFilter()
    .WithPreWarmTrigger();

IHost host = builder.Build();
await host.RunAsync();