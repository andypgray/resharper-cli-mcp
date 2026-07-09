using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Pipeline;

/// <summary>
///     Drives a real MCP client against the server over in-memory pipes to prove the input-coercion
///     pipeline end to end: the schema the custom converters would erase is re-injected
///     (<see cref="Zphil.ReSharperCli.Pipeline.CoercingToolRegistration" />), malformed-but-obvious
///     argument shapes are silently repaired, an invalid enum surfaces the friendly valid-values error
///     without a logged warning (the <c>FindUserError</c> unwrap), and a hallucinated argument key is
///     rejected by <see cref="Zphil.ReSharperCli.Pipeline.UnknownParameterGuard" />.
/// </summary>
public sealed class CoercionIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListTools_CleanupFilesParameter_AdvertisesArrayOfStringsAndStaysRequired()
    {
        // Arrange
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Act
        var tools = await harness.Client.ListToolsAsync(cancellationToken: Ct);

        // Assert — the schema-erasure guard: StringArrayCoercerFactory would collapse this to {} without
        // the re-injection step. type/items must survive, and files must stay schema-required.
        McpClientTool cleanup = tools.Single(tool => tool.Name == "resharper_cleanup");
        JsonElement files = PropertySchema(cleanup, "files");
        files.GetProperty("type").GetString().ShouldBe("array");
        files.GetProperty("items").GetProperty("type").GetString().ShouldBe("string");
        RequiredProperties(cleanup).ShouldContain("files");
    }

    [Fact]
    public async Task ListTools_InspectSeverityParameter_AdvertisesStringWithEnumValues()
    {
        // Arrange
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Act
        var tools = await harness.Client.ListToolsAsync(cancellationToken: Ct);

        // Assert — EnumValidationConverterFactory erases the enum's shape; re-injection restores both the
        // string type AND the value list, so the allowed severities travel in the schema itself. This is
        // the guard that lets the description prose stay free of the enum names (no drift-in-prose test).
        McpClientTool inspect = tools.Single(tool => tool.Name == "resharper_inspect");
        JsonElement severity = PropertySchema(inspect, "severity");
        severity.GetProperty("type").GetString().ShouldBe("string");
        EnumValues(severity).ShouldBe(["Suggestion", "Warning", "Error"]);
    }

    [Fact]
    public async Task ListTools_ScalarStringParameters_AdvertiseStringType()
    {
        // Arrange — the scalar-string reinjection is load-bearing: this project's exporter erases every
        // string?/string parameter to a bare {} under StringCoercerFactory. Assert a representative one
        // on each tool advertises a plain "string" (not {}, and not a ["string","null"] union).
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Act
        var tools = await harness.Client.ListToolsAsync(cancellationToken: Ct);

        // Assert — inspect.solutionPath (nullable, no default) and cleanup.profile (a real default).
        McpClientTool inspect = tools.Single(tool => tool.Name == "resharper_inspect");
        McpClientTool cleanup = tools.Single(tool => tool.Name == "resharper_cleanup");
        PropertySchema(inspect, "solutionPath").GetProperty("type").GetString().ShouldBe("string");
        PropertySchema(cleanup, "profile").GetProperty("type").GetString().ShouldBe("string");
    }

    [Fact]
    public async Task CallTool_CleanupFilesAsBareString_CoercesToSingleFile()
    {
        // Arrange — a bare string where files : string[] is advertised. Must be single-coerced.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        PlantSolution(harness.Environment, "App.sln");
        PlantFile(harness.Environment, "src/A.cs");
        List<string>? cleanupArguments = null;
        RouteJb(harness.ProcessRunner, arguments => cleanupArguments = [.. arguments]);

        // Act
        CallToolResult result = await harness.Client.CallToolAsync(
            "resharper_cleanup",
            new Dictionary<string, object?> { ["files"] = "src/A.cs" },
            cancellationToken: Ct);

        // Assert — succeeds, and the single file reached jb's --include.
        result.IsError.ShouldNotBe(true);
        cleanupArguments.ShouldNotBeNull();
        cleanupArguments.ShouldContain("--include=src/A.cs");
    }

    [Fact]
    public async Task CallTool_CleanupFilesAsStringifiedJsonArray_CoercesToBothFiles()
    {
        // Arrange — the dominant malformed shape: a JSON array encoded as a string.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        PlantSolution(harness.Environment, "App.sln");
        PlantFile(harness.Environment, "src/A.cs");
        PlantFile(harness.Environment, "src/B.cs");
        List<string>? cleanupArguments = null;
        RouteJb(harness.ProcessRunner, arguments => cleanupArguments = [.. arguments]);

        // Act
        CallToolResult result = await harness.Client.CallToolAsync(
            "resharper_cleanup",
            new Dictionary<string, object?> { ["files"] = """["src/A.cs","src/B.cs"]""" },
            cancellationToken: Ct);

        // Assert — both files reached jb.
        result.IsError.ShouldNotBe(true);
        cleanupArguments.ShouldNotBeNull();
        cleanupArguments.ShouldContain("--include=src/A.cs;src/B.cs");
    }

    [Fact]
    public async Task CallTool_InspectSolutionPathAsSingleElementArray_UnwrapsToScalar()
    {
        // Arrange — a single-element array where solutionPath : string? is advertised. Must be unwrapped.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        PlantSolution(harness.Environment, "App.sln");
        string expectedSolution = Path.GetFullPath("App.sln", harness.Environment.CurrentDirectory);
        List<string>? inspectArguments = null;
        RouteJb(
            harness.ProcessRunner,
            arguments => inspectArguments = [.. arguments],
            Fixtures.ReadSarif("inspect-sample.json"));

        // Act
        CallToolResult result = await harness.Client.CallToolAsync(
            "resharper_inspect",
            new Dictionary<string, object?> { ["solutionPath"] = new[] { "App.sln" } },
            cancellationToken: Ct);

        // Assert — the unwrapped scalar resolved to the solution and reached jb.
        result.IsError.ShouldNotBe(true);
        inspectArguments.ShouldNotBeNull();
        inspectArguments.ShouldContain(expectedSolution);
    }

    [Fact]
    public async Task CallTool_InspectInvalidSeverity_ReturnsValidValuesErrorAndLogsNothing()
    {
        // Arrange — the coercer throws inside the SDK argument binder, which wraps it in JsonException(s).
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        PlantSolution(harness.Environment, "App.sln");
        RouteJb(harness.ProcessRunner);

        // Act
        CallToolResult result = await harness.Client.CallToolAsync(
            "resharper_inspect",
            new Dictionary<string, object?> { ["severity"] = "HIGH" },
            cancellationToken: Ct);

        // Assert — the friendly valid-values message surfaced, and FindUserError kept it out of the log.
        result.IsError.ShouldBe(true);
        string text = TextOf(result);
        text.ShouldContain("HIGH");
        text.ShouldContain("Valid values: Suggestion, Warning, Error");
        harness.Logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task CallTool_UnknownParameterKey_ReturnsGuardErrorAndLogsNothing()
    {
        // Arrange
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Act — "file" is a typo of "files"; the guard rejects it before binding.
        CallToolResult result = await harness.Client.CallToolAsync(
            "resharper_cleanup",
            new Dictionary<string, object?> { ["file"] = "src/A.cs" },
            cancellationToken: Ct);

        // Assert — actionable error naming the bad key and the tool, and nothing logged (expected).
        result.IsError.ShouldBe(true);
        string text = TextOf(result);
        text.ShouldContain("\"file\"");
        text.ShouldContain("resharper_cleanup");
        harness.Logs.Warnings.ShouldBeEmpty();
    }

    private static JsonElement PropertySchema(McpClientTool tool, string propertyName)
    {
        return tool.JsonSchema.GetProperty("properties").GetProperty(propertyName);
    }

    /// <summary>The values in a parameter schema's <c>enum</c> array, in declaration order.</summary>
    private static IReadOnlyList<string> EnumValues(JsonElement propertySchema)
    {
        return propertySchema.GetProperty("enum").EnumerateArray().Select(element => element.GetString()!).ToList();
    }

    /// <summary>The names in a tool's input-schema <c>required</c> array, or empty when it has none.</summary>
    private static IReadOnlyList<string> RequiredProperties(McpClientTool tool)
    {
        if (!tool.JsonSchema.TryGetProperty("required", out JsonElement required)
            || required.ValueKind != JsonValueKind.Array)
            return [];

        return required.EnumerateArray().Select(element => element.GetString()!).ToList();
    }

    private static string TextOf(CallToolResult result)
    {
        return result.Content.OfType<TextContentBlock>().First().Text;
    }

    private static void PlantSolution(FakeEnvironment environment, string fileName)
    {
        File.WriteAllText(Path.Combine(environment.CurrentDirectory, fileName), string.Empty);
    }

    private static void PlantFile(FakeEnvironment environment, string relativePath)
    {
        string fullPath = Path.Combine(environment.CurrentDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, string.Empty);
    }

    /// <summary>
    ///     Routes the process-runner substitute by jb sub-command: the version probe succeeds; the
    ///     inspectcode/cleanupcode run is handed to <paramref name="onCommand" /> for argument capture, and
    ///     inspectcode additionally writes <paramref name="inspectSarif" /> to its <c>-o=</c> path when
    ///     supplied. Everything succeeds with exit code 0.
    /// </summary>
    private static void RouteJb(
        IProcessRunner processRunner,
        Action<IReadOnlyList<string>>? onCommand = null,
        string? inspectSarif = null)
    {
        processRunner
            .RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var arguments = callInfo.ArgAt<IReadOnlyList<string>>(1);

                if (arguments.Contains("--version")) return new ProcessResult(0, "Version: 2026.1.2", string.Empty);

                onCommand?.Invoke(arguments);

                if (arguments.Count > 0 && arguments[0] == "inspectcode" && inspectSarif is not null)
                    File.WriteAllText(OutputPathFrom(arguments), inspectSarif);

                return new ProcessResult(0, string.Empty, string.Empty);
            });
    }

    private static string OutputPathFrom(IReadOnlyList<string> arguments)
    {
        string arg = arguments.First(a => a.StartsWith("-o=", StringComparison.Ordinal));
        return arg["-o=".Length..];
    }
}