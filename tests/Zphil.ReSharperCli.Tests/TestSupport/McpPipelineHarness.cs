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
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Connects a real MCP client and server in-process over a pair of pipes, with no child process and no real
///     stdio, composed as <c>Program.cs</c> composes the production server with its two seams faked. A test
///     drives the <c>tools/call</c> pipeline end to end and asserts both on what the client sees and on what
///     the server logged.
/// </summary>
/// <remarks>
///     Per test instance and parallel-safe: no mutable statics, and the <see cref="FakeEnvironment" /> stands
///     in for the process environment, so no test needs a <c>[Collection]</c>.
/// </remarks>
internal sealed class McpPipelineHarness : IAsyncDisposable
{
    /// <summary>How often anything in this graph that reports itself does so.</summary>
    /// <remarks>
    ///     Brisk, so a test watching progress sees a second beat in tens of milliseconds rather than at
    ///     <see cref="JbRunProgress.HeartbeatInterval" />. One number for the runner and the cache reset alike: a
    ///     test watching progress is watching whichever of them it called, and two intervals would be two ways
    ///     for one to sit out a wait the other does not.
    /// </remarks>
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

    /// <summary>Gets the connected client, past the <c>initialize</c> handshake.</summary>
    public McpClient Client { get; }

    /// <summary>
    ///     Gets the server's environment seam, for setting <c>MAX_MCP_OUTPUT_TOKENS</c> or planting a solution in
    ///     its working directory.
    /// </summary>
    public FakeEnvironment Environment { get; }

    /// <summary>
    ///     Gets the process seam the server spawns <c>jb</c> through, which a test stubs (probe,
    ///     <c>inspectcode</c>, <c>cleanupcode</c>) before a tool call.
    /// </summary>
    public IProcessRunner ProcessRunner { get; }

    /// <summary>Gets everything the server logged through its <see cref="ILoggerFactory" />.</summary>
    public CapturingLoggerProvider Logs { get; }

    /// <summary>
    ///     Gets the server's background cache pre-warm, the same instance the client's first message triggers.
    /// </summary>
    /// <remarks>Await its <c>Finished</c> to make a test that opted in deterministic.</remarks>
    public CacheWarmer Warmer { get; }

    /// <summary>Gets every frame the server wrote, in the order it wrote them.</summary>
    /// <remarks>
    ///     Always on rather than opt-in: it costs one extra copy of this session's own frames, freed with the
    ///     harness, and no test should have to opt in to have the wire it is already using be observable.
    /// </remarks>
    public WireLog Wire { get; }

    public async ValueTask DisposeAsync()
    {
        // Order matters: closing the client sends EOF, which ends the single-session server's RunAsync and
        // starts host shutdown. The stop is bounded so a stuck one cannot hang the suite.
        try
        {
            await Client.DisposeAsync();
            await StopAsync(_host);
        }
        finally
        {
            Environment.Dispose();
        }
    }

    /// <summary>
    ///     Builds the host, starts it, and connects a client over the pipe pair.
    /// </summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <param name="preWarm">
    ///     Whether the background cache pre-warm is left on. Off by default, because the first message of the
    ///     client's handshake triggers it: left on, every test in the suite would start a speculative <c>jb</c>
    ///     run that races whatever the test arranges afterwards and logs a warning of its own against the
    ///     unstubbed substitute process runner, breaking the "exactly one warning" assertions this harness
    ///     exists to make.
    /// </param>
    /// <param name="arrange">
    ///     Runs against the two seams before the host starts, so a test that opted into the pre-warm can plant
    ///     its solution and stub <c>jb</c> before the client's first message reaches the server.
    /// </param>
    /// <param name="processRunner">
    ///     The process seam, defaulting to a fresh NSubstitute double. Passing the real
    ///     <see cref="Zphil.ReSharperCli.Execution.ProcessRunner" /> is the only way to drive a real <c>jb</c>
    ///     through the whole <c>tools/call</c> pipeline.
    /// </param>
    public static async Task<McpPipelineHarness> StartAsync(
        CancellationToken cancellationToken,
        bool preWarm = false,
        Action<FakeEnvironment, IProcessRunner>? arrange = null,
        IProcessRunner? processRunner = null)
    {
        FakeEnvironment environment = new();
        IHost? host = null;
        try
        {
            processRunner ??= Substitute.For<IProcessRunner>();
            CapturingLoggerProvider logs = new();

            if (!preWarm) environment.SetVariable(CacheWarmer.EnableVariable, "off");

            // Created before WithStreamServerTransport, which constructs the server transport eagerly at
            // registration.
            Pipe clientToServer = new();
            Pipe serverToClient = new();
            WireLog wire = new();

            HostApplicationBuilder builder = Host.CreateApplicationBuilder();

            builder.Logging.CaptureInto(logs);

            // The Program.cs service graph, with the two seams faked.
            builder.Services.AddSingleton<IEnvironment>(environment);
            builder.Services.AddSingleton(processRunner);
            builder.Services.AddSingleton<JbLocator>();
            builder.Services.AddSingleton<ConfigResolver>();

            // The lock and the runner take the run cap, a value rather than a service, so the composition root
            // builds both by factory; the loggers come from the host's own factory, so everything the graph
            // writes reaches the capturing provider above.
            builder.Services.AddSingleton(provider => JbRunners.Lock(logs: provider.GetRequiredService<ILoggerFactory>()));
            builder.Services.AddSingleton<JbRunYield>();
            builder.Services.AddSingleton(provider => JbRunners.Create(
                processRunner,
                provider.GetRequiredService<JbRunLock>(),
                provider.GetRequiredService<JbRunYield>(),
                logs: provider.GetRequiredService<ILoggerFactory>(),
                heartbeat: BriskHeartbeat));
            builder.Services.AddSingleton<InspectService>();

            string filterRoot = environment.CreateTempDirectory();
            builder.Services.AddSingleton(provider => JbRunners.Filter(
                filterRoot, logs: provider.GetRequiredService<ILoggerFactory>()));
            builder.Services.AddSingleton<CleanupService>();

            // By factory for the heartbeat alone: a reset reports the wait it is serving out, so registered by
            // type it would beat at the production interval and a test watching one would sit out a whole beat.
            builder.Services.AddSingleton(provider => JbRunners.Reset(
                provider.GetRequiredService<JbRunLock>(),
                provider.GetRequiredService<JbRunYield>(),
                provider.GetRequiredService<ILoggerFactory>(),
                BriskHeartbeat));

            // The writer must be registered whether or not a test asks for a report: CoercingToolRegistration
            // activates ResharperTools per tools/call, so a missing registration surfaces as every call returning
            // an error result rather than as anything a build would catch. Its root is a temp directory of this
            // harness's own, deleted with the environment.
            string reportRoot = environment.CreateTempDirectory();
            builder.Services.AddSingleton(provider => new InspectReportWriter(
                reportRoot,
                provider.GetRequiredService<ILogger<InspectReportWriter>>()));
            builder.Services.AddSingleton<CacheWarmer>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<CacheWarmer>());

            builder.Services
                .AddResharperMcpServer()
                .WithStreamServerTransport(
                    clientToServer.Reader.AsStream(), new WireTapStream(serverToClient.Writer.AsStream(), wire));

            host = builder.Build();

            arrange?.Invoke(environment, processRunner);

            await host.StartAsync(cancellationToken);

            StreamClientTransport clientTransport = new(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream());
            var client = await McpClient.CreateAsync(clientTransport, cancellationToken: cancellationToken);

            return new McpPipelineHarness(
                host, client, environment, processRunner, logs, host.Services.GetRequiredService<CacheWarmer>(), wire);
        }
        catch
        {
            // Handed back on a throw as on success: a failed start would otherwise leave the environment's temp
            // directories behind and, once the host has started, a server reading a pipe nobody completes.
            try
            {
                if (host is not null) await StopAsync(host);
            }
            finally
            {
                environment.Dispose();
            }

            throw;
        }
    }

    /// <summary>Stops <paramref name="host" />, bounded so a stuck stop cannot hang the suite, and disposes it.</summary>
    private static async Task StopAsync(IHost host)
    {
        using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(30));
        await host.StopAsync(stopTimeout.Token);
        host.Dispose();
    }
}