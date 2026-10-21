using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Infrastructure;
using Zphil.ReSharperCli.Services;
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
    ///     <paramref name="logs" /> is wired through the whole graph when a test means to assert on what it
    ///     logged; omitted, every class logs to a <see cref="NullLogger{T}" />. <paramref name="reportRoot" /> is
    ///     where <see cref="InspectReportWriter" /> puts the files it writes. The real temp path is a safe default
    ///     because nothing is written unless a call passes <c>report</c>; a test that passes <c>report</c> should
    ///     pass a root of its own, such as <c>FakeEnvironment.CreateTempDirectory()</c>, which is deleted with the
    ///     environment.
    /// </remarks>
    public static ResharperTools Build(
        IProcessRunner processRunner,
        IEnvironment environment,
        ILoggerFactory? logs = null,
        string? reportRoot = null)
    {
        JbLocator jbLocator = new(processRunner, environment, Logs.For<JbLocator>(logs));
        ConfigResolver configResolver = new(jbLocator, environment, Logs.For<ConfigResolver>(logs));

        // One lock and one yield for the whole graph, shared by the runner and the reset as the composition
        // root shares them.
        JbRunLock runLock = JbRunners.Lock(logs: logs);
        JbRunYield runYield = JbRunners.Yield(logs);
        JbRunner jbRunner = JbRunners.Create(processRunner, runLock, runYield, logs: logs);

        InspectService inspectService = new(jbRunner);
        CleanupService cleanupService = new(jbRunner, Logs.For<CleanupService>(logs));
        CacheResetService cacheResetService = JbRunners.Reset(runLock, runYield, logs);
        InspectReportWriter reportWriter = new(
            reportRoot ?? Path.GetTempPath(), Logs.For<InspectReportWriter>(logs));
        return new ResharperTools(
            configResolver,
            inspectService,
            cleanupService,
            cacheResetService,
            reportWriter,
            environment,
            Logs.For<ResharperTools>(logs));
    }
}