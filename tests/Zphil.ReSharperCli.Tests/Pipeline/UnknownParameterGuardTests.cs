using System.Reflection;
using System.Text.Json;
using ModelContextProtocol;
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
        // primary constructors, so the context-bound *method* parameters in this server are the
        // CancellationToken every tool takes and the IProgress<> every tool now takes too; IsJsonBound
        // encodes exactly that, independently of the guard's own predicate. A newly introduced
        // context-bound parameter type will (correctly) trip this test, forcing an update here — and
        // in UnknownParameterGuard only if its exclusion is not already generic there, as
        // IProgress<>'s was when the progress parameter arrived.
        List<string> failures = [];

        foreach ((MethodInfo method, McpServerToolAttribute attribute) in ToolAttributeDiscovery.GetToolMethods())
        {
            if (attribute.Name is not { } toolName) continue;

            Dictionary<string, JsonElement> arguments = method.GetParameters()
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

    // Independent oracle for "is this a JSON-bound parameter": the context-bound method-parameter
    // types in this server are CancellationToken and the IProgress<ProgressNotificationValue> the
    // SDK manufactures for a run that reports its advance. Deliberately NOT calling the guard's own
    // predicate, so a divergence is observable — and spelled by closed type rather than by open
    // generic, so a second IProgress<T> of some other T would still trip this.
    private static bool IsJsonBound(ParameterInfo p)
    {
        if (p.Name is null) return false;

        Type type = p.ParameterType;

        return type != typeof(CancellationToken) && type != typeof(IProgress<ProgressNotificationValue>);
    }
}