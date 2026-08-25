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
    /// <remarks>
    ///     <c>logs</c> is wired through the whole graph when a test means to assert on what it logged;
    ///     omitted, every class logs into <see cref="NullLoggerFactory" />.
    /// </remarks>
    public static ResharperTools Build(
        IProcessRunner processRunner,
        IEnvironment environment,
        ILoggerFactory? logs = null)
    {
        JbLocator jbLocator = new(processRunner, environment, LoggerFor<JbLocator>(logs));
        ConfigResolver configResolver = new(jbLocator, environment, LoggerFor<ConfigResolver>(logs));

        // One lock and one yield for the whole graph, exactly as the composition root registers them: a
        // cache reset with a lock of its own would serialize against nothing and could delete a generation
        // mid-run, and one with a yield of its own would queue behind the pre-warm it is meant to displace.
        JbRunLock runLock = JbRunners.Lock(logs: logs);
        JbRunYield runYield = JbRunners.Yield(logs);
        JbRunner jbRunner = JbRunners.Create(processRunner, runLock, runYield, logs: logs);

        InspectService inspectService = new(jbRunner);
        CleanupService cleanupService = new(jbRunner, LoggerFor<CleanupService>(logs));
        CacheResetService cacheResetService = JbRunners.Reset(runLock, runYield, logs);
        return new ResharperTools(
            configResolver,
            inspectService,
            cleanupService,
            cacheResetService,
            environment,
            LoggerFor<ResharperTools>(logs));
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
        ILogger<CacheWarmer> logger,
        ILoggerFactory? logs = null)
    {
        JbLocator jbLocator = new(processRunner, environment, LoggerFor<JbLocator>(logs));
        ConfigResolver configResolver = new(jbLocator, environment, LoggerFor<ConfigResolver>(logs));
        JbRunner jbRunner = JbRunners.Create(processRunner, logs: logs);
        InspectService inspectService = new(jbRunner);
        CacheWarmer warmer = new(configResolver, inspectService, jbRunner, environment, logger);
        return new WarmerGraph(warmer, jbRunner);
    }

    private static ILogger<T> LoggerFor<T>(ILoggerFactory? logs)
    {
        return logs is null ? NullLogger<T>.Instance : logs.CreateLogger<T>();
    }
}

/// <summary>
///     A warmer and the runner underneath it. The runner is handed back because the two are wired to each
///     other: a foreground run hitting its cap is what re-arms the warmer, and only a run driven through
///     <em>this</em> runner reaches <em>that</em> warmer.
/// </summary>
internal sealed record WarmerGraph(CacheWarmer Warmer, JbRunner Runner);