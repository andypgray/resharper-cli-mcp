using Microsoft.Extensions.Hosting;

namespace Zphil.ReSharperCli.Infrastructure;

/// <summary>
///     Provides the host builder the server composes its services on.
/// </summary>
/// <remarks>
///     The server reads no host configuration, so it takes none: no <c>appsettings</c> files, no watcher over the
///     working directory they would be read from, and no environment or command-line configuration. With the
///     defaults on, that watcher is a recursive file watcher over the MCP client's working directory, which is the
///     user's repository, and a burst of writes there reloads every default settings file at once. The server's
///     own variables are read through <see cref="IEnvironment" /> and its flags from <c>args</c> in
///     <c>Program.cs</c>, so none of them depends on the host's configuration.
/// </remarks>
internal static class ServerHostBuilder
{
    /// <summary>Creates a host builder with every host default turned off.</summary>
    /// <remarks>
    ///     Takes no arguments: the builder adds <see cref="HostApplicationBuilderSettings.Args" /> as command-line
    ///     configuration even with the defaults off.
    /// </remarks>
    public static HostApplicationBuilder Create()
    {
        HostApplicationBuilderSettings settings = new() { DisableDefaults = true };

        return Host.CreateApplicationBuilder(settings);
    }
}