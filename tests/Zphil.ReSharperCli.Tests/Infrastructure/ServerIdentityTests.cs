using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Infrastructure;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Infrastructure;

/// <summary>
///     Guards the <c>serverInfo</c> a client is handed on <c>initialize</c>. The name and title are what a
///     client displays; the icon is embedded, so a rename of <c>assets/icon-128.png</c> or of its manifest
///     resource id would otherwise surface as a client failing to start rather than as a test failure.
///     <para>
///         The icon travels as a <c>data:</c> URI rather than a URL because this server speaks stdio: it
///         listens on no authority for a client to match a remote icon against, and it has no install path
///         that survives a tool install, a <c>dnx</c> cache and a container image alike. That choice is what
///         the decode assertion below pins — a URL creeping in here would pass "the field is populated" and
///         still show nothing.
///     </para>
/// </summary>
public sealed class ServerIdentityTests
{
    private const string DataUriPrefix = "data:image/png;base64,";

    [Fact]
    public void Create_IdentifiesTheServerByNameTitleAndVersion()
    {
        // Act
        Implementation identity = ServerIdentity.Create();

        // Assert
        identity.Name.ShouldBe("resharper-cli-mcp");
        identity.Title.ShouldBe("ReSharper CLI Tools (unofficial)");
        identity.Version.ShouldBe(ServerVersion.SemVer);
    }

    [Fact]
    public void Create_ReturnsAFreshInstance()
    {
        // Act
        Implementation first = ServerIdentity.Create();
        Implementation second = ServerIdentity.Create();

        // Assert — Implementation is mutable, and the harness stands up a second host in this same
        // process. A shared instance would let one host's SDK edit the other's handshake.
        first.ShouldNotBeSameAs(second);
    }

    [Fact]
    public void Create_CarriesThePngIconAsADataUri()
    {
        // Act
        Icon icon = ServerIdentity.Create().Icons.ShouldHaveSingleItem();

        // Assert
        icon.MimeType.ShouldBe("image/png");
        icon.Sizes.ShouldBe(["128x128"]);
        icon.Source.ShouldStartWith(DataUriPrefix);

        // No theme: the mark is drawn on its own opaque tile, so it reads on a light or a dark background.
        icon.Theme.ShouldBeNull();
    }

    [Fact]
    public void Create_EmbedsTheRenderCommittedUnderAssets()
    {
        // Arrange — the render the repository ships, which every other icon site names by path.
        byte[] committed = File.ReadAllBytes(Path.Combine(RepoRoot.Location, "assets", "icon-128.png"));

        // Act
        string source = ServerIdentity.Create().Icons.ShouldHaveSingleItem().Source;
        byte[] embedded = Convert.FromBase64String(source[DataUriPrefix.Length..]);

        // Assert — the handshake ships the same bytes as the file, so the mark cannot fork between the
        // channel a client renders and the one a directory does.
        embedded.ShouldBe(committed);
    }
}