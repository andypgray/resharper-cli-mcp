using Microsoft.Extensions.Logging.Abstractions;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Infrastructure;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Builds a real <see cref="ResharperTools" /> wired to the concrete service graph, composed over the two
///     fakeable seams (<see cref="IProcessRunner" /> and <see cref="IEnvironment" />), so a test exercises the
///     whole path from tool to config, service and process without a DI container.
/// </summary>
internal static class ToolHarness
{
    /// <summary>Builds the tools over the given seams.</summary>
    /// <remarks>
    ///     Every class logs to a <see cref="NullLogger{T}" />; a test that asserts on what the graph logged uses
    ///     <see cref="McpPipelineHarness.Logs" />. <paramref name="reportRoot" /> is where
    ///     <see cref="InspectReportWriter" /> puts the files it writes, defaulting to a temp directory of
    ///     <paramref name="environment" />'s own, so a report is deleted with the environment and never lands
    ///     in, or prunes, the directory the real server writes to.
    /// </remarks>
    public static ResharperTools Build(
        IProcessRunner processRunner,
        FakeEnvironment environment,
        string? reportRoot = null)
    {
        JbLocator jbLocator = new(processRunner, environment, NullLogger<JbLocator>.Instance);
        ConfigResolver configResolver = new(jbLocator, environment, NullLogger<ConfigResolver>.Instance);

        // One lock and one yield for the whole graph, shared by the runner and the reset as the composition
        // root shares them.
        JbRunLock runLock = JbRunners.Lock();
        JbRunYield runYield = JbRunners.Yield();
        JbRunner jbRunner = JbRunners.Create(processRunner, runLock, runYield);

        InspectService inspectService = new(jbRunner);
        CleanupService cleanupService = new(jbRunner, NullLogger<CleanupService>.Instance);
        CacheResetService cacheResetService = JbRunners.Reset(runLock, runYield);
        InspectReportWriter reportWriter = new(
            reportRoot ?? environment.CreateTempDirectory(), NullLogger<InspectReportWriter>.Instance);
        return new ResharperTools(
            configResolver,
            inspectService,
            cleanupService,
            cacheResetService,
            reportWriter,
            environment,
            NullLogger<ResharperTools>.Instance);
    }
}