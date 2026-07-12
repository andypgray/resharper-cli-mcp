using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Infrastructure;
using Zphil.ReSharperCli.Pipeline;
using Zphil.ReSharperCli.Prompts;
using Zphil.ReSharperCli.Resources;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     A real MCP client and server connected in-process over a pair of pipes — no child process, no real
///     stdio — composed exactly as <c>Program.cs</c> composes the production server: the same DI graph, both
///     fakeable seams faked, the global call-tool filter installed. Lets integration tests drive the
///     <c>tools/call</c> pipeline end to end and assert both on what the client sees and on what the server
///     logged. Per-test-instance and parallel-safe: no shared statics and no environment mutation (the
///     <see cref="FakeEnvironment" /> stands in for the real process environment), matching the repo's
///     zero-<c>[Collection]</c> design. Dispose closes the client, stops the host, and deletes temp dirs.
/// </summary>
internal sealed class McpPipelineHarness : IAsyncDisposable
{
    private readonly IHost _host;

    private McpPipelineHarness(
        IHost host,
        McpClient client,
        FakeEnvironment environment,
        IProcessRunner processRunner,
        CapturingLoggerProvider logs)
    {
        _host = host;
        Client = client;
        Environment = environment;
        ProcessRunner = processRunner;
        Logs = logs;
    }

    /// <summary>The connected client, past the <c>initialize</c> handshake — call <c>ListTools</c>/<c>CallTool</c> on it.</summary>
    public McpClient Client { get; }

    /// <summary>
    ///     The server's environment seam: set <c>MAX_MCP_OUTPUT_TOKENS</c>, plant a solution in its working directory,
    ///     etc.
    /// </summary>
    public FakeEnvironment Environment { get; }

    /// <summary>The single <c>jb</c> process-runner substitute — stub it (probe, inspectcode, cleanupcode) before a tool call.</summary>
    public IProcessRunner ProcessRunner { get; }

    /// <summary>Everything the server logged through its <see cref="ILoggerFactory" /> during the session.</summary>
    public CapturingLoggerProvider Logs { get; }

    public async ValueTask DisposeAsync()
    {
        // Dispose order matters: closing the client sends EOF, which ends the single-session server's
        // RunAsync and triggers host shutdown; then stop the host (bounded so a stuck stop can't hang the
        // suite); finally delete the environment's temp directories.
        await Client.DisposeAsync();

        using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(30));
        await _host.StopAsync(stopTimeout.Token);
        _host.Dispose();

        Environment.Dispose();
    }

    /// <summary>
    ///     Builds the host, starts it, and connects a client over the pipe pair. Mirrors the
    ///     <c>AddMcpServer</c> + <c>WithCoercingTools</c> + <c>WithGlobalCallToolFilter</c> composition in
    ///     <c>Program.cs</c>, swapping the stdio transport for a stream transport over in-memory pipes.
    /// </summary>
    public static async Task<McpPipelineHarness> StartAsync(CancellationToken cancellationToken)
    {
        FakeEnvironment environment = new();
        var processRunner = Substitute.For<IProcessRunner>();
        CapturingLoggerProvider logs = new();

        // Two unidirectional pipes: client -> server and server -> client. Created before
        // WithStreamServerTransport, which constructs the server transport eagerly at registration.
        Pipe clientToServer = new();
        Pipe serverToClient = new();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        // Capture everything the server logs and nothing else, so "no warning" / "exactly one warning"
        // assertions see the filter alone rather than the default console/debug providers.
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);

        // The Program.cs service graph, with the two seams faked.
        builder.Services.AddSingleton<IEnvironment>(environment);
        builder.Services.AddSingleton(processRunner);
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
            .WithCoercingTools()
            .WithPrompts<ResharperPrompts>()
            .WithResources<ResharperResources>()
            .WithGlobalCallToolFilter()
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());

        IHost host = builder.Build();
        await host.StartAsync(cancellationToken);

        StreamClientTransport clientTransport = new(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream());
        var client = await McpClient.CreateAsync(clientTransport, cancellationToken: cancellationToken);

        return new McpPipelineHarness(host, client, environment, processRunner, logs);
    }
}