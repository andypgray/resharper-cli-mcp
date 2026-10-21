using ModelContextProtocol.Client;
using Xunit;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Provides one MCP pipeline and one <c>tools/list</c> per test class, for a class that only reads the
///     advertised surface.
/// </summary>
/// <remarks>
///     Every case in such a class asks its question of the same answer, so a harness per case would start a
///     server per case to read one response, and each extra server competes with the timing-sensitive tests
///     running in parallel.
/// </remarks>
public sealed class AdvertisedToolsFixture : IAsyncLifetime
{
    private McpPipelineHarness? _harness;

    /// <summary>Gets the advertised tool list, read once.</summary>
    public IList<McpClientTool> Tools { get; private set; } = [];

    public async ValueTask InitializeAsync()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        _harness = await McpPipelineHarness.StartAsync(cancellationToken);
        Tools = await _harness.Client.ListToolsAsync(cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null) await _harness.DisposeAsync();
    }
}