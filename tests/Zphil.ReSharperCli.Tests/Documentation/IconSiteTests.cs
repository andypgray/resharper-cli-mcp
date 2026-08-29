using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Documentation;

/// <summary>
///     Holds every file that names the project's mark to a render committed under <c>assets/</c>. The
///     channels are independent — NuGet packs the icon, the MCP registry links it, Cursor's manifest points
///     a listing at it — so none of them substitutes for another and a rename under <c>assets/</c> breaks
///     each one silently and separately. Only the handshake is absent here: it embeds the bytes rather than
///     naming a path, and <c>ServerIdentityTests</c> pins it against the same file.
/// </summary>
public sealed partial class IconSiteTests
{
    /// <summary>The raw host that serves this repository's committed files, and the only one an icon may cite.</summary>
    private const string RawContentPrefix = "https://raw.githubusercontent.com/andypgray/resharper-cli-mcp/main/";

    /// <summary>The registry rejects an icon source over this length, which rules a base64 data URI out of that channel.</summary>
    private const int RegistryIconSourceMaxLength = 255;

    [GeneratedRegex(@"<PackageIcon>(?<file>[^<]+)</PackageIcon>")]
    private static partial Regex PackageIconElement();

    [GeneratedRegex(@"<None\s+Include=""(?<include>[^""]*assets[^""]+)""\s+Pack=""true""\s+PackagePath=""(?<path>[^""]+)""")]
    private static partial Regex PackedAssetItem();

    [Fact]
    public void NuGetPackageIcon_NamesAnAssetThatThePackIncludes()
    {
        // Arrange
        string csproj = Csproj();

        // Act
        Match declared = PackageIconElement().Match(csproj);
        Match packed = PackedAssetItem().Match(csproj);

        // Assert — <PackageIcon> is a path *inside* the package, so it only resolves if a Pack item puts
        // the file there. The two are written apart in the csproj and are one fact.
        declared.Success.ShouldBeTrue("The csproj must declare a <PackageIcon> for nuget.org to render one.");
        packed.Success.ShouldBeTrue("The csproj must pack the icon file the <PackageIcon> names.");

        string iconFile = declared.Groups["file"].Value;
        packed.Groups["include"].Value.ShouldEndWith(
            iconFile,
            Case.Sensitive,
            "The packed asset must be the file <PackageIcon> names, or the package declares an icon it "
            + "does not carry and nuget.org rejects it.");
        packed.Groups["path"].Value.ShouldBe(
            "/",
            "<PackageIcon> names the icon at the package root, so the Pack item must put it there. A "
            + "backslash-separated PackagePath is also wrong on the Linux release runner.");
        File.Exists(Path.Combine(RepoRoot.Location, "assets", iconFile)).ShouldBeTrue(
            $"assets/{iconFile} must exist for the pack to include it.");
    }

    [Fact]
    public void CursorManifestLogo_NamesACommittedRender()
    {
        // Act — the path is repo-relative, and Cursor resolves it against raw.githubusercontent.
        string? logo = RepoManifest.ReadString(".cursor-plugin/plugin.json", "/logo");

        // Assert
        string path = logo.ShouldNotBeNull(".cursor-plugin/plugin.json is the only manifest that can carry a logo.");
        File.Exists(Path.Combine(RepoRoot.Location, path)).ShouldBeTrue(
            $"{path} must be committed: a listing importer fetches this path from the repository and "
            + "renders nothing when it 404s.");
    }

    [Fact]
    public void RegistryIcons_AreHttpsUrlsWithinTheRegistryCap()
    {
        // Act
        IReadOnlyList<RegistryIcon> icons = RegistryIcons();

        // Assert
        icons.ShouldNotBeEmpty("server.json must declare icons for a registry listing to render one.");

        foreach (RegistryIcon icon in icons)
        {
            icon.Source.ShouldStartWith(
                "https://",
                Case.Sensitive,
                "The registry requires HTTPS icon sources.");
            icon.Source.Length.ShouldBeLessThanOrEqualTo(
                RegistryIconSourceMaxLength,
                $"The registry caps an icon source at {RegistryIconSourceMaxLength} characters; "
                + $"\"{icon.Source}\" is {icon.Source.Length}.");
            icon.MimeType.ShouldBe(
                "image/png",
                "PNG is the one media type the icons spec requires every client to render. SVG is only a "
                + "SHOULD, and carries a scripting caveat a consumer is told to take precautions over.");
        }
    }

    [Fact]
    public void RegistryIcons_PointAtCommittedRendersOfTheDeclaredSize()
    {
        // Act
        IReadOnlyList<RegistryIcon> icons = RegistryIcons();

        // Assert
        foreach (RegistryIcon icon in icons)
        {
            icon.Source.ShouldStartWith(
                RawContentPrefix,
                Case.Sensitive,
                "An icon must be served from this repository's own raw content, which is what makes the "
                + "assertions below able to check it offline — and what keeps the mark on a host the "
                + "project controls.");

            string repoRelativePath = icon.Source[RawContentPrefix.Length..];
            string committedPath = Path.Combine(RepoRoot.Location, repoRelativePath);
            File.Exists(committedPath).ShouldBeTrue(
                $"{repoRelativePath} must be committed, or the URL 404s the moment the registry renders it.");

            PngRender.SizeOf(File.ReadAllBytes(committedPath)).ShouldBe(
                icon.Sizes.ShouldHaveSingleItem(),
                $"The size declared for {repoRelativePath} must be the render's own, or a client picking "
                + "by size gets the wrong file.");
        }
    }

    private static string Csproj()
    {
        return File.ReadAllText(
            Path.Combine(RepoRoot.Location, "src", "Zphil.ReSharperCli", "Zphil.ReSharperCli.csproj"));
    }

    /// <summary>The <c>icons</c> array declared by the MCP registry manifest.</summary>
    private static IReadOnlyList<RegistryIcon> RegistryIcons()
    {
        string manifest = File.ReadAllText(Path.Combine(RepoRoot.Location, ".mcp", "server.json"));
        using JsonDocument document = JsonDocument.Parse(manifest);

        return document.RootElement.GetProperty("icons")
            .EnumerateArray()
            .Select(icon => new RegistryIcon(
                icon.GetProperty("src").GetString()!,
                icon.GetProperty("mimeType").GetString()!,
                icon.GetProperty("sizes").EnumerateArray().Select(size => size.GetString()!).ToList()))
            .ToList();
    }

    private sealed record RegistryIcon(string Source, string MimeType, IReadOnlyList<string> Sizes);
}