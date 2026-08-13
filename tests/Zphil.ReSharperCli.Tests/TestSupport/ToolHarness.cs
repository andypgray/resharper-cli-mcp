using Microsoft.Extensions.Logging;
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
    public static ResharperTools Build(IProcessRunner processRunner, IEnvironment environment)
    {
        JbLocator jbLocator = new(processRunner, environment);
        ConfigResolver configResolver = new(jbLocator, environment);

        // One lock for the whole graph, exactly as the composition root registers it: a cache reset with a
        // lock of its own would serialize against nothing and could delete a generation mid-run.
        JbRunLock runLock = new(JbRunTimeout.Default);
        JbRunner jbRunner = JbRunners.Create(processRunner, runLock);

        InspectService inspectService = new(jbRunner);
        CleanupService cleanupService = new(jbRunner);
        CacheResetService cacheResetService = new(runLock);
        return new ResharperTools(configResolver, inspectService, cleanupService, cacheResetService, environment);
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
        JbLocator jbLocator = new(processRunner, environment);
        ConfigResolver configResolver = new(jbLocator, environment);
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