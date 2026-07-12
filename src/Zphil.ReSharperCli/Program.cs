using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
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
// before host building so a catastrophic startup failure still lands in the post-mortem log.
SerilogConfiguration.InitializeFileLogger();
SerilogConfiguration.RegisterCrashHandlers();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.AddSerilogLogging();

// The two fakeable seams plus the pure/concrete graph composed over them — all singletons.
builder.Services.AddSingleton<IEnvironment, SystemEnvironment>();
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<JbLocator>();
builder.Services.AddSingleton<ConfigResolver>();
builder.Services.AddSingleton<InspectService>();
builder.Services.AddSingleton<CleanupService>();

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
    .WithGlobalCallToolFilter();

IHost host = builder.Build();
await host.RunAsync();