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
        InspectService inspectService = new(processRunner);
        CleanupService cleanupService = new(processRunner);
        return new ResharperTools(configResolver, inspectService, cleanupService, environment);
    }
}