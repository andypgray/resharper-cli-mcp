using Microsoft.Extensions.DependencyInjection;
using Zphil.ReSharperCli.Infrastructure;
using Zphil.ReSharperCli.Prompts;
using Zphil.ReSharperCli.Resources;

namespace Zphil.ReSharperCli.Pipeline;

/// <summary>
///     Registers the MCP server this tool presents: what a client sees, and the filters every message and tool
///     call passes through.
/// </summary>
/// <remarks>
///     Everything but the transport, which the caller adds. A registration that changes what a client sees belongs
///     here rather than beside a transport call, or one host builds a server the other does not.
/// </remarks>
internal static class ResharperMcpServer
{
    /// <summary>
    ///     Adds the server's instructions, identity, tools, prompts, resources and filters, and returns the builder
    ///     for the caller's transport.
    /// </summary>
    public static IMcpServerBuilder AddResharperMcpServer(this IServiceCollection services)
    {
        return services
            .AddMcpServer(options =>
            {
                options.ServerInstructions = ServerInstructions.Text;
                options.ServerInfo = ServerIdentity.Create();
            })
            .WithCoercingTools()
            .WithPrompts<ResharperPrompts>()
            .WithResources<ResharperResources>()
            .WithGlobalCallToolFilter()
            .WithPreWarmTrigger();
    }
}