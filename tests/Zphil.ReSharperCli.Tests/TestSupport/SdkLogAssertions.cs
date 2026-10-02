using Shouldly;
using Zphil.ReSharperCli.Tests.TestDoubles;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>Assertions on the log lines the MCP SDK writes itself, outside this server's call filter.</summary>
/// <remarks>
///     By the template's property names rather than its rendered sentence, as <see cref="LogEntry" /> explains,
///     so an SDK release that rewords the line breaks no test here.
/// </remarks>
[ShouldlyMethods]
internal static class SdkLogAssertions
{
    /// <summary>
    ///     The warning the SDK's session handler writes around a <c>tools/call</c> that ends in a JSON-RPC error,
    ///     here one that threw <typeparamref name="TException" />.
    /// </summary>
    public static void ShouldBeTheSdksFailedToolCall<TException>(this LogEntry warning)
        where TException : Exception
    {
        warning.Category.ShouldStartWith("ModelContextProtocol.");
        warning.Property("Method").ShouldBe("tools/call");
        warning.Exception.ShouldBeAssignableTo<TException>();
    }
}