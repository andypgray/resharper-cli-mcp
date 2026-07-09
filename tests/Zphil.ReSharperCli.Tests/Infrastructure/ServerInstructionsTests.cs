using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Tests.Infrastructure;

/// <summary>
///     Guards the embedded <c>server-instructions.md</c> resource. A rename of the file or its manifest
///     resource id would otherwise surface only as a runtime failure when a client connects; these
///     load-time assertions turn it into a test failure instead.
/// </summary>
public sealed class ServerInstructionsTests
{
    [Fact]
    public void Text_LoadsAndIsNonTrivial()
    {
        // Assert
        ServerInstructions.Text.Length.ShouldBeGreaterThan(100);
    }

    [Fact]
    public void Text_ContainsUnofficialDisclaimer()
    {
        // Assert
        ServerInstructions.Text.ShouldContain("unofficial");
        ServerInstructions.Text.ShouldContain("not affiliated with or endorsed by JetBrains");
    }
}