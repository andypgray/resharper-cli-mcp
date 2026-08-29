using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Documentation;

/// <summary>
///     Pins every file that names the release version to the csproj <c>&lt;Version&gt;</c>, the single number
///     behind them all. <c>release.yml</c> re-checks the same set when a tag is pushed, so a site that has
///     drifted fails here rather than at the release.
///     <para>
///         A marketplace entry tracks a commit SHA, so a floating <c>dotnet dnx Zphil.ReSharperCli</c> would
///         mean the SHA does not determine the server code a plugin user runs — two installs of one commit,
///         weeks apart, execute different builds. Pinning the argument fixes that for a new install. It
///         reaches an existing one only if the manifest <c>version</c> moves too, because Claude Code ships
///         an installed plugin an update only when that field changes; frozen, it would leave every existing
///         install on its install-time pin for good. A dnx pin and its manifest version therefore move
///         together, with the csproj as the single number behind them.
///     </para>
/// </summary>
public sealed partial class VersionSiteTests
{
    private const string PackageId = "Zphil.ReSharperCli";
    private const string PinPrefix = $"{PackageId}@";

    private const string RegistryRationale =
        "mcp-publisher publishes this file as-is, so a field that does not follow the csproj <Version> "
        + "registers a version that is not this release. release.yml fails the tag when they disagree.";

    private const string PluginRationale =
        "Claude Code ships an installed plugin an update only when the manifest version changes, so a "
        + "version that does not follow the csproj <Version> strands existing installs on an older pin.";

    [GeneratedRegex(@"<Version>(?<version>[^<]+)</Version>")]
    private static partial Regex CsprojVersion();

    [Theory]
    [InlineData(".mcp/server.json", "/version", RegistryRationale)]
    [InlineData(".mcp/server.json", "/packages/0/version", RegistryRationale)]
    [InlineData(".claude-plugin/plugin.json", "/version", PluginRationale)]
    public void ManifestVersionField_MatchesTheCsprojVersion(string manifestPath, string jsonPointer, string because)
    {
        // Arrange
        string declaredVersion = DeclaredCsprojVersion();
        string manifest = File.ReadAllText(Path.Combine(RepoRoot.Location, manifestPath));

        // Act
        using JsonDocument document = JsonDocument.Parse(manifest);
        string? version = Resolve(document.RootElement, jsonPointer).GetString();

        // Assert
        version.ShouldBe(declaredVersion, because);
    }

    [Theory]
    [InlineData(".claude-plugin/plugin.json")]
    public void DnxLauncher_PinsThePackageToTheCsprojVersion(string manifestPath)
    {
        // Arrange
        string declaredVersion = DeclaredCsprojVersion();
        string manifest = File.ReadAllText(Path.Combine(RepoRoot.Location, manifestPath));

        // Act
        using JsonDocument document = JsonDocument.Parse(manifest);
        string? pinnedArgument = LauncherArguments(document)
            .FirstOrDefault(argument => argument.StartsWith(PinPrefix, StringComparison.Ordinal));

        // Assert
        string pin = pinnedArgument.ShouldNotBeNull(
            $"{manifestPath} must name the package as '{PinPrefix}<version>' in a launcher's args. A bare "
            + $"'{PackageId}' resolves whatever NuGet holds at launch time, so the commit a user installs "
            + "from stops determining the server code.");
        pin[PinPrefix.Length..].ShouldBe(
            declaredVersion,
            $"The dnx pin in {manifestPath} is a version site: it rolls with the csproj <Version>, and "
            + "release.yml fails the tag when it has not.");
    }

    /// <summary>
    ///     The <c>&lt;Version&gt;</c> the csproj declares — the one number every site here is measured against.
    /// </summary>
    private static string DeclaredCsprojVersion()
    {
        string csproj = File.ReadAllText(
            Path.Combine(RepoRoot.Location, "src", "Zphil.ReSharperCli", "Zphil.ReSharperCli.csproj"));
        Match declaredVersion = CsprojVersion().Match(csproj);
        declaredVersion.Success.ShouldBeTrue(
            "The csproj must declare a <Version> for the version sites to agree with.");

        return declaredVersion.Groups["version"].Value;
    }

    /// <summary>
    ///     Walks a JSON pointer — <c>/packages/0/version</c> — from <paramref name="root" />, indexing an
    ///     array when a segment is a number and reading a property otherwise.
    /// </summary>
    private static JsonElement Resolve(JsonElement root, string jsonPointer)
    {
        JsonElement current = root;

        foreach (string segment in jsonPointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
            current = int.TryParse(segment, out int index)
                ? current[index]
                : current.GetProperty(segment);

        return current;
    }

    /// <summary>
    ///     Every string in every server's <c>args</c> array. Enumerating the servers rather than naming the
    ///     <c>resharper</c> key means renaming that key cannot make the assertions above vacuous.
    /// </summary>
    private static IEnumerable<string> LauncherArguments(JsonDocument manifest)
    {
        JsonElement servers = manifest.RootElement.GetProperty("mcpServers");

        foreach (JsonProperty server in servers.EnumerateObject())
        {
            if (!server.Value.TryGetProperty("args", out JsonElement arguments))
                continue;

            foreach (JsonElement argument in arguments.EnumerateArray())
                if (argument.GetString() is { } text)
                    yield return text;
        }
    }
}
