using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Documentation;

/// <summary>
///     Pins the "unofficial — not affiliated with or endorsed by JetBrains" notice to the three surfaces
///     CLAUDE.md's respectful-wrapping requirement names: the NuGet package <c>Description</c>, the README's
///     opening paragraph, and <c>.mcp/server.json</c>. The name uses "ReSharper" descriptively, so these are
///     requirements rather than decoration — and they are the reason the always-resident server instructions
///     need not carry the notice as well (see <c>ServerInstructionsTests</c>). Also guards the MCP registry's
///     hard 100-character, ASCII-counted cap on the <c>server.json</c> description, which today sits at 99:
///     without this the overflow would surface at registry-publish time rather than at build time.
/// </summary>
public sealed partial class RespectfulWrappingTests
{
    private const string Disclaimer = "not affiliated with or endorsed by JetBrains";
    private const int RegistryDescriptionMaxLength = 100;

    [GeneratedRegex(@"<Description>(?<text>.*?)</Description>", RegexOptions.Singleline)]
    private static partial Regex CsprojDescription();

    [Fact]
    public void NuGetPackageDescription_CarriesTheUnofficialNotice()
    {
        // Arrange
        string csproj = File.ReadAllText(
            Path.Combine(RepoRoot.Location, "src", "Zphil.ReSharperCli", "Zphil.ReSharperCli.csproj"));

        // Act
        Match match = CsprojDescription().Match(csproj);

        // Assert
        match.Success.ShouldBeTrue("The csproj must declare a <Description> for the NuGet package.");
        match.Groups["text"].Value.ShouldContain(Disclaimer);
    }

    [Fact]
    public void ReadmeOpeningParagraph_CarriesTheUnofficialNotice()
    {
        // Arrange
        string readme = File.ReadAllText(Path.Combine(RepoRoot.Location, "README.md"));

        // Act — the first prose block, skipping the title, the mcp-name comment, and the badge row.
        string openingParagraph = readme
            .ReplaceLineEndings("\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .First(IsProse);

        // Assert
        openingParagraph.ShouldContain(Disclaimer);
    }

    [Fact]
    public void McpRegistryManifest_CarriesTheUnofficialNoticeWithinTheRegistryCap()
    {
        // Arrange
        string manifest = File.ReadAllText(Path.Combine(RepoRoot.Location, ".mcp", "server.json"));

        // Act
        using JsonDocument document = JsonDocument.Parse(manifest);
        string description = document.RootElement.GetProperty("description").GetString()!;

        // Assert
        description.ShouldContain(Disclaimer);

        // The registry rejects descriptions over 100 characters and counts them as ASCII, so a stray em
        // dash or curly quote would both inflate the count and be miscounted. Assert ASCII first.
        Ascii.IsValid(description).ShouldBeTrue($"server.json description must be ASCII-only: \"{description}\"");
        description.Length.ShouldBeLessThanOrEqualTo(RegistryDescriptionMaxLength);
    }

    private static bool IsProse(string block)
    {
        return !block.StartsWith('#')
               && !block.StartsWith("<!--", StringComparison.Ordinal)
               && !block.Contains("img.shields.io", StringComparison.Ordinal);
    }
}