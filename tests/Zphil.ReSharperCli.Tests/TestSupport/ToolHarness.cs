using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Infrastructure;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Builds a real <see cref="ResharperTools" /> wired to the concrete service graph, composed over the
///     only two fakeable seams (<see cref="IProcessRunner" /> and <see cref="IEnvironment" />). Lets the
///     tool tests exercise the full tool → config → service → process path without a DI container.
/// </summary>
internal static class ToolHarness
{
    /// <param name="processRunner">The process seam every jb spawn in the graph goes through.</param>
    /// <param name="environment">The environment seam: variables, working directory, home directory.</param>
    /// <param name="logs">
    ///     Wired through the whole graph when a test means to assert on what it logged; omitted, every class
    ///     logs into <see cref="NullLoggerFactory" />.
    /// </param>
    /// <param name="reportRoot">
    ///     Where <see cref="InspectReportWriter" /> puts the files it writes. Defaulting to the real temp path
    ///     looks careless and is not: nothing is written unless a call passes <c>report</c>, and no test does
    ///     unless it also passes a root of its own. A test that does should pass
    ///     <c>FakeEnvironment.CreateTempDirectory()</c>, which is deleted with the environment.
    /// </param>
    public static ResharperTools Build(
        IProcessRunner processRunner,
        IEnvironment environment,
        ILoggerFactory? logs = null,
        string? reportRoot = null)
    {
        JbLocator jbLocator = new(processRunner, environment, Logs.For<JbLocator>(logs));
        ConfigResolver configResolver = new(jbLocator, environment, Logs.For<ConfigResolver>(logs));

        // One lock and one yield for the whole graph, exactly as the composition root registers them: a
        // cache reset with a lock of its own would serialize against nothing and could delete a generation
        // mid-run, and one with a yield of its own would queue behind the pre-warm it is meant to displace.
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

    /// <summary>
    ///     The same graph as far as <see cref="InspectService" />, topped with the background
    ///     <see cref="CacheWarmer" />, so its tests drive the real discovery → lock → process path rather than
    ///     a stubbed one. The lock is private to the returned warmer: a test that needs the cache generation
    ///     held takes the lock <em>file</em>, which is what another server process looks like anyway.
    /// </summary>
    public static WarmerGraph BuildCacheWarmer(
        IProcessRunner processRunner,
        IEnvironment environment,
        ILogger<CacheWarmer> logger)
    {
        JbLocator jbLocator = new(processRunner, environment, NullLogger<JbLocator>.Instance);
        ConfigResolver configResolver = new(jbLocator, environment, NullLogger<ConfigResolver>.Instance);
        JbRunner jbRunner = JbRunners.Create(processRunner);
        InspectService inspectService = new(jbRunner);
        CacheWarmer warmer = new(configResolver, inspectService, jbRunner, environment, logger);
        return new WarmerGraph(warmer, jbRunner);
    }
}

/// <summary>
///     A warmer and the runner underneath it. The runner is handed back because the two are wired to each
///     other: a foreground run hitting its cap is what re-arms the warmer, and only a run driven through
///     <em>this</em> runner reaches <em>that</em> warmer.
/// </summary>
internal sealed record WarmerGraph(CacheWarmer Warmer, JbRunner Runner);