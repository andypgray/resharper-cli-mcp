using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Pipeline;

namespace Zphil.ReSharperCli.Tests.Pipeline;

/// <summary>
///     Tests for <see cref="UnknownParameterGuard" />: a JSON argument key matching no
///     declared parameter is surfaced as an actionable, self-correcting error instead of
///     being silently dropped by the SDK's <c>UnmappedMemberHandling = Skip</c>.
/// </summary>
public sealed class UnknownParameterGuardTests
{
    // Value is never inspected by the guard (only keys are), so a single shared dummy
    // suffices; the document is intentionally kept alive for the class lifetime.
    private static readonly JsonElement DummyValue = JsonDocument.Parse("null").RootElement;

    [Fact]
    public void Validate_UnknownKeyOnRealTool_NamesBadKeyToolAndValidList()
    {
        // Act — "file" is the classic singular typo of the "files" parameter.
        string? message = UnknownParameterGuard.Validate(
            "resharper_cleanup",
            new Dictionary<string, JsonElement> { ["file"] = DummyValue });

        // Assert — names the bad key (quoted), the tool, and the real parameter list.
        message.ShouldNotBeNull();
        message.ShouldContain("\"file\"");
        message.ShouldContain("resharper_cleanup");
        message.ShouldContain("files");
        message.ShouldContain("profile");
    }

    [Fact]
    public void Validate_EveryDeclaredParameter_ReturnsNull()
    {
        // Arrange — independently reflect every tool's JSON parameter names. Services arrive via
        // primary constructors, so the only context-bound *method* parameter in this server is
        // CancellationToken; IsJsonBound encodes exactly that, independently of the guard's own
        // predicate. A newly introduced context-bound parameter type will (correctly) trip this
        // test, forcing a matching update here and in UnknownParameterGuard.
        List<string> failures = [];

        foreach (MethodInfo method in ToolAttributeDiscovery.GetToolMethods())
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>()?.Name is not { } toolName) continue;

            var arguments = method.GetParameters()
                .Where(IsJsonBound)
                .ToDictionary(p => p.Name!, _ => DummyValue);

            string? message = UnknownParameterGuard.Validate(toolName, arguments);
            if (message is not null) failures.Add($"{toolName}: {message}");
        }

        // Assert — every real parameter name is accepted; any failure is schema drift.
        failures.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_CaseInsensitiveKey_ReturnsNull()
    {
        // Act — a casing slip binds anyway under Web defaults, so it must not be flagged.
        string? message = UnknownParameterGuard.Validate(
            "resharper_cleanup",
            new Dictionary<string, JsonElement> { ["Files"] = DummyValue });

        // Assert
        message.ShouldBeNull();
    }

    [Fact]
    public void Validate_KnownKeysOnInspect_ReturnsNull()
    {
        // Act — a representative subset of resharper_inspect's real parameters.
        string? message = UnknownParameterGuard.Validate(
            "resharper_inspect",
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
    public void Validate_UnknownToolName_ReturnsNull()
    {
        // Act — unknown-tool dispatch is the SDK's concern; the guard never blocks it.
        string? message = UnknownParameterGuard.Validate(
            "no_such_tool",
            new Dictionary<string, JsonElement> { ["whatever"] = DummyValue });

        // Assert
        message.ShouldBeNull();
    }

    [Fact]
    public void Validate_NullArguments_ReturnsNull()
    {
        UnknownParameterGuard.Validate("resharper_inspect", null).ShouldBeNull();
    }

    [Fact]
    public void Validate_EmptyArguments_ReturnsNull()
    {
        UnknownParameterGuard.Validate(
            "resharper_inspect",
            new Dictionary<string, JsonElement>()).ShouldBeNull();
    }

    [Theory]
    [InlineData("path")]
    [InlineData("paths")]
    [InlineData("file")]
    public void Validate_HallucinatedKeyOnCleanup_ReturnsError(string hallucinatedKey)
    {
        // Act — the keys a model reaches for instead of the real "files" parameter.
        string? message = UnknownParameterGuard.Validate(
            "resharper_cleanup",
            new Dictionary<string, JsonElement> { [hallucinatedKey] = DummyValue });

        // Assert
        message.ShouldNotBeNull();
        message.ShouldContain($"\"{hallucinatedKey}\"");
        message.ShouldContain("resharper_cleanup");
    }

    // Independent oracle for "is this a JSON-bound parameter": the only context-bound
    // method-parameter type in this server is CancellationToken. Deliberately NOT calling the
    // guard's own predicate, so a divergence is observable.
    private static bool IsJsonBound(ParameterInfo p)
    {
        return p.Name is not null && p.ParameterType != typeof(CancellationToken);
    }
}