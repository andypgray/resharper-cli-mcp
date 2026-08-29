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
    /// <summary>
    ///     How often anything in this graph that reports itself does so. One number for the runner and the
    ///     cache reset alike: a test watching progress is watching whichever of them it called, and two
    ///     intervals would be two ways for one to sit out a wait the other does not.
    /// </summary>
    private static readonly TimeSpan BriskHeartbeat = TimeSpan.FromMilliseconds(50);

    private readonly IHost _host;

    private McpPipelineHarness(
        IHost host,
        McpClient client,
        FakeEnvironment environment,
        IProcessRunner processRunner,
        CapturingLoggerProvider logs,
        CacheWarmer warmer,
        WireLog wire)
    {
        _host = host;
        Client = client;
        Environment = environment;
        ProcessRunner = processRunner;
        Logs = logs;
        Warmer = warmer;
        Wire = wire;
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

    /// <summary>
    ///     The server's background cache pre-warm — the same instance the <c>initialized</c> notification
    ///     triggers. Await its <c>Finished</c> to make a test that opted in deterministic.
    /// </summary>
    public CacheWarmer Warmer { get; }

    /// <summary>
    ///     Every frame the server wrote, in the order it wrote them — the only place an ordering claim about
    ///     notifications is a fact rather than an inference about thread-pool scheduling. Always on rather than
    ///     opt-in: it costs one extra copy of this session's own frames, freed with the harness, and no test
    ///     should have to opt in to have the wire it is already using be observable.
    /// </summary>
    public WireLog Wire { get; }

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
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <param name="preWarm">
    ///     Whether the background cache pre-warm is left on. It defaults to <em>off</em> because the first
    ///     message of the client's handshake triggers it, which would otherwise start a speculative <c>jb</c>
    ///     run in every test in the suite — racing whatever the test arranges afterwards, and logging a
    ///     warning of its own against the unstubbed substitute process runner, which breaks the "exactly one
    ///     warning" assertions this harness exists to make. Tests about the pre-warm itself opt in, and
    ///     everything else gets a deterministic session and exercises the off switch for free.
    /// </param>
    /// <param name="arrange">
    ///     Runs against the two seams before the host starts, so a test that opted into the pre-warm can plant
    ///     its solution and stub <c>jb</c> before the client's first message reaches the server.
    /// </param>
    /// <param name="processRunner">
    ///     The process seam, defaulting to a fresh NSubstitute double. The contract suite passes the real
    ///     <see cref="ProcessRunner" /> instead, which is the only way to drive a genuine <c>jb</c> through the
    ///     whole <c>tools/call</c> pipeline — every other test in the suite wants the double, and gets it by
    ///     saying nothing.
    /// </param>
    public static async Task<McpPipelineHarness> StartAsync(
        CancellationToken cancellationToken,
        bool preWarm = false,
        Action<FakeEnvironment, IProcessRunner>? arrange = null,
        IProcessRunner? processRunner = null)
    {
        FakeEnvironment environment = new();
        processRunner ??= Substitute.For<IProcessRunner>();
        CapturingLoggerProvider logs = new();

        if (!preWarm) environment.SetVariable(CacheWarmer.EnableVariable, "off");

        // Two unidirectional pipes: client -> server and server -> client. Created before
        // WithStreamServerTransport, which constructs the server transport eagerly at registration.
        Pipe clientToServer = new();
        Pipe serverToClient = new();
        WireLog wire = new();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        // Capture everything the server logs and nothing else, so "no warning" / "exactly one warning"
        // assertions see the filter alone rather than the default console/debug providers. Down to Trace,
        // because the filter's own call envelope is Debug and the default minimum of Information would hide
        // it — and would hide it in a way that looks exactly like a filter that never logged.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);

        // The Program.cs service graph, with the two seams faked.
        builder.Services.AddSingleton<IEnvironment>(environment);
        builder.Services.AddSingleton(processRunner);
        builder.Services.AddSingleton<JbLocator>();
        builder.Services.AddSingleton<ConfigResolver>();

        // The lock and the runner are the two the composition root builds by factory, because the run cap is a
        // value rather than a service; the loggers come from the host's own factory, so everything the graph
        // writes reaches the capturing provider above.
        builder.Services.AddSingleton(provider => new JbRunLock(
            JbRunTimeout.Default, provider.GetRequiredService<ILogger<JbRunLock>>()));
        builder.Services.AddSingleton<JbRunYield>();
        // A brisk heartbeat, so a test watching progress notifications sees a second beat in tens of
        // milliseconds instead of paying the production ten seconds per run of the suite.
        builder.Services.AddSingleton(provider => JbRunners.Create(
            processRunner,
            provider.GetRequiredService<JbRunLock>(),
            provider.GetRequiredService<JbRunYield>(),
            logs: provider.GetRequiredService<ILoggerFactory>(),
            heartbeat: BriskHeartbeat));
        builder.Services.AddSingleton<InspectService>();
        builder.Services.AddSingleton<CleanupService>();

        // By factory for the heartbeat alone: a reset reports the wait it is serving out, so registered by
        // type it would beat at the production interval and a test watching one would sit out ten seconds.
        builder.Services.AddSingleton(provider => JbRunners.Reset(
            provider.GetRequiredService<JbRunLock>(),
            provider.GetRequiredService<JbRunYield>(),
            provider.GetRequiredService<ILoggerFactory>(),
            BriskHeartbeat));

        // Registering the writer is not optional even though no harness test asks for a report:
        // CoercingToolRegistration activates ResharperTools per tools/call, so a missing registration
        // surfaces as every call returning an error result rather than as anything a build would catch.
        // Its root is a temp directory of this harness's own, deleted with the environment.
        builder.Services.AddSingleton(provider => new InspectReportWriter(
            environment.CreateTempDirectory(),
            provider.GetRequiredService<ILogger<InspectReportWriter>>()));
        builder.Services.AddSingleton<CacheWarmer>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<CacheWarmer>());

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInstructions = ServerInstructions.Text;
                options.ServerInfo = ServerIdentity.Create();
            })
            .WithCoercingTools()
            .WithPrompts<ResharperPrompts>()
            .WithResources<ResharperResources>()
            .WithGlobalCallToolFilter()
            .WithPreWarmTrigger()
            .WithStreamServerTransport(
                clientToServer.Reader.AsStream(), new WireTapStream(serverToClient.Writer.AsStream(), wire));

        IHost host = builder.Build();

        // Before the host starts, and so before the client's `initialized` can reach the server: a pre-warm
        // test has to have its solution planted and its jb stubbed by then.
        arrange?.Invoke(environment, processRunner);

        await host.StartAsync(cancellationToken);

        StreamClientTransport clientTransport = new(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream());
        var client = await McpClient.CreateAsync(clientTransport, cancellationToken: cancellationToken);

        return new McpPipelineHarness(
            host, client, environment, processRunner, logs, host.Services.GetRequiredService<CacheWarmer>(), wire);
    }
}