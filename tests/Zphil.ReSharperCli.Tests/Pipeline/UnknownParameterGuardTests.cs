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
    // Value is never inspected by the guard (only keys are), so a single shared dummy
    // suffices; the document is intentionally kept alive for the class lifetime.
    private static readonly JsonElement DummyValue = JsonDocument.Parse("null").RootElement;

    [Theory]
    [InlineData("file")]
    [InlineData("path")]
    [InlineData("paths")]
    [InlineData("Files")]
    public void Validate_KeyThatBindsNothingOnCleanup_NamesItAndTheValidList(string key)
    {
        // Act — the keys a model reaches for instead of the real "files" parameter, and its wrong-case twin:
        // the SDK binds each argument by its parameter's exact name, so a casing slip binds nothing either.
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
    public void Validate_NullArguments_ReturnsNull()
    {
        UnknownParameterGuard.Validate(ToolNamed("resharper_inspect"), null).ShouldBeNull();
    }

    [Fact]
    public void Validate_EmptyArguments_ReturnsNull()
    {
        UnknownParameterGuard.Validate(
            ToolNamed("resharper_inspect"),
            new Dictionary<string, JsonElement>()).ShouldBeNull();
    }

    /// <summary>The advertised tool named <paramref name="name" />, as the guard is handed it.</summary>
    private Tool ToolNamed(string name)
    {
        return advertised.Tools.Single(tool => tool.Name == name).ProtocolTool;
    }
}