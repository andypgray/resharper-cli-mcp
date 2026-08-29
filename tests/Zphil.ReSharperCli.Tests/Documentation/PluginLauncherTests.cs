using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Documentation;

/// <summary>
///     Pins both of the plugin manifest's version sites to the csproj <c>&lt;Version&gt;</c>: the version its
///     <c>dnx</c> argument resolves, and the manifest <c>version</c> that decides whether an installed plugin
///     ever sees it.
///     <para>
///         A marketplace entry tracks a commit SHA, so a floating <c>dotnet dnx Zphil.ReSharperCli</c> would
///         mean the SHA does not determine the server code a plugin user runs — two installs of one commit,
///         weeks apart, execute different builds. Pinning the argument fixes that for a new install. It
///         reaches an existing one only if the manifest <c>version</c> moves too, because Claude Code ships
///         an installed plugin an update only when that field changes; frozen, it would leave every existing
///         install on its install-time pin for good. The two therefore move together, with the csproj as the
///         single number behind them, and <c>release.yml</c> re-checks both before a tag can publish.
///     </para>
/// </summary>
public sealed partial class PluginLauncherTests
{
    private const string PackageId = "Zphil.ReSharperCli";
    private const string PinPrefix = $"{PackageId}@";

    [GeneratedRegex(@"<Version>(?<version>[^<]+)</Version>")]
    private static partial Regex CsprojVersion();

    [Fact]
    public void PluginDnxLauncher_PinsThePackageToTheCsprojVersion()
    {
        // Arrange
        string csproj = File.ReadAllText(
            Path.Combine(RepoRoot.Location, "src", "Zphil.ReSharperCli", "Zphil.ReSharperCli.csproj"));
        Match declaredVersion = CsprojVersion().Match(csproj);
        declaredVersion.Success.ShouldBeTrue("The csproj must declare a <Version> for the plugin pin to agree with.");

        string manifest = File.ReadAllText(
            Path.Combine(RepoRoot.Location, ".claude-plugin", "plugin.json"));

        // Act
        using JsonDocument document = JsonDocument.Parse(manifest);
        string? pinnedArgument = LauncherArguments(document)
            .FirstOrDefault(argument => argument.StartsWith(PinPrefix, StringComparison.Ordinal));

        // Assert
        string pin = pinnedArgument.ShouldNotBeNull(
            $"The plugin launcher must name the package as '{PinPrefix}<version>'. A bare '{PackageId}' resolves "
            + "whatever NuGet holds at launch time, so the marketplace commit stops determining the server code.");
        pin[PinPrefix.Length..].ShouldBe(
            declaredVersion.Groups["version"].Value,
            "The plugin's dnx pin is a version site: it rolls with the csproj <Version>, and release.yml "
            + "fails the tag when it has not.");
    }

    [Fact]
    public void PluginManifestVersion_MatchesTheCsprojVersion()
    {
        // Arrange
        string csproj = File.ReadAllText(
            Path.Combine(RepoRoot.Location, "src", "Zphil.ReSharperCli", "Zphil.ReSharperCli.csproj"));
        Match declaredVersion = CsprojVersion().Match(csproj);
        declaredVersion.Success.ShouldBeTrue("The csproj must declare a <Version> for the plugin version to agree with.");

        string manifest = File.ReadAllText(
            Path.Combine(RepoRoot.Location, ".claude-plugin", "plugin.json"));

        // Act
        using JsonDocument document = JsonDocument.Parse(manifest);
        string? version = document.RootElement.GetProperty("version").GetString();

        // Assert
        version.ShouldBe(
            declaredVersion.Groups["version"].Value,
            "Claude Code ships an installed plugin an update only when the manifest version changes, so a "
            + "version that does not follow the csproj <Version> strands existing installs on an older pin.");
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