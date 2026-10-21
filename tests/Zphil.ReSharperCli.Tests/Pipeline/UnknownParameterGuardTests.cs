using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Pipeline;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Pipeline;

/// <summary>
///     Pins <see cref="UnknownParameterGuard" /> against the tools a real <c>tools/list</c> advertises, so
///     every case reads the schema a client is given.
/// </summary>
public sealed class UnknownParameterGuardTests(AdvertisedToolsFixture advertised)
    : IClassFixture<AdvertisedToolsFixture>
{
    // The guard reads keys only, never values, so one shared dummy value serves every case. Its document is
    // never disposed: disposing a JsonDocument invalidates the elements it handed out.
    private static readonly JsonElement DummyValue = JsonDocument.Parse("null").RootElement;

    /// <summary>
    ///     Gets the argument keys of cleanup calls that leave out its required <c>files</c>: no arguments at all, an
    ///     empty object, and only valid keys.
    /// </summary>
    public static TheoryData<string[]?> CleanupKeysWithoutFiles => new() { (string[]?)null, { [] }, { ["profile", "solutionPath"] } };

    [Theory]
    [InlineData("file")]
    [InlineData("path")]
    [InlineData("paths")]
    [InlineData("Files")]
    public void Validate_KeyThatBindsNothingOnCleanup_NamesItAndTheValidList(string key)
    {
        // Act — the keys a model reaches for instead of the real "files" parameter, and its wrong-case twin:
        // the SDK binds each argument by its parameter's exact name, so a casing slip binds nothing either. Each
        // call also omits the required "files", so these rows pin that an unknown key is reported first.
        string? message = UnknownParameterGuard.Validate(
            ToolNamed("resharper_cleanup"),
            new Dictionary<string, JsonElement> { [key] = DummyValue });

        // Assert
        message.ShouldBe($"Unknown parameter \"{key}\" on \"resharper_cleanup\". Valid: files, profile, solutionPath.");
    }

    [Fact]
    public void Validate_KnownKeysOnInspect_ReturnsNull()
    {
        // Act — a representative subset of resharper_inspect's real parameters.
        string? message = UnknownParameterGuard.Validate(
            ToolNamed("resharper_inspect"),
            new Dictionary<string, JsonElement>
            {
                ["solutionPath"] = DummyValue,
                ["files"] = DummyValue,
                ["severity"] = DummyValue
            });

        // Assert
        message.ShouldBeNull();
    }

    [Fact]
    public void Validate_NullArgumentsOnInspect_ReturnsNull()
    {
        UnknownParameterGuard.Validate(ToolNamed("resharper_inspect"), null).ShouldBeNull();
    }

    [Fact]
    public void Validate_EmptyArgumentsOnInspect_ReturnsNull()
    {
        UnknownParameterGuard.Validate(
            ToolNamed("resharper_inspect"),
            new Dictionary<string, JsonElement>()).ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(CleanupKeysWithoutFiles))]
    public void Validate_CleanupWithoutFiles_NamesTheMissingKeyAndTheValidList(string[]? keys)
    {
        // Act
        string? message = UnknownParameterGuard.Validate(
            ToolNamed("resharper_cleanup"),
            keys?.ToDictionary(key => key, _ => DummyValue));

        // Assert
        message.ShouldBe("Missing required parameter \"files\" on \"resharper_cleanup\". Valid: files, profile, solutionPath.");
    }

    [Fact]
    public void Validate_RequiredKeyPresentWithNullValue_ReturnsNull()
    {
        // Act — the key is present, so the tool's own empty-list error answers it, not the guard.
        string? message = UnknownParameterGuard.Validate(
            ToolNamed("resharper_cleanup"),
            new Dictionary<string, JsonElement> { ["files"] = DummyValue });

        // Assert
        message.ShouldBeNull();
    }

    /// <summary>Returns the advertised tool named <paramref name="name" />, as the guard is handed it.</summary>
    private Tool ToolNamed(string name)
    {
        return advertised.Tools.Single(tool => tool.Name == name).ProtocolTool;
    }
}