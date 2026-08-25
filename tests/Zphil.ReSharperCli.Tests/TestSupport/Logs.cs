using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Tests.TestDoubles;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     The logging factory a test hands to a service graph when it means to assert on what that graph logged.
/// </summary>
/// <remarks>
///     <see cref="LogLevel.Trace" /> rather than the factory default of <see cref="LogLevel.Information" />,
///     because most of what this server logs is <c>Debug</c> and a test asserting on a <c>Debug</c> line would
///     otherwise fail for the one reason that has nothing to do with the code under test.
/// </remarks>
internal static class Logs
{
    public static ILoggerFactory Capturing(CapturingLoggerProvider provider)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(provider);
        });
    }
}