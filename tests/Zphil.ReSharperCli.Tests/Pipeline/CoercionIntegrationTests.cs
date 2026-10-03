using System.Text.Json;
using ModelContextProtocol;
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
///     pipeline end to end.
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
        IList<McpClientTool> tools = await harness.Client.ListToolsAsync(cancellationToken: Ct);

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
        IList<McpClientTool> tools = await harness.Client.ListToolsAsync(cancellationToken: Ct);

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
        IList<McpClientTool> tools = await harness.Client.ListToolsAsync(cancellationToken: Ct);

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
        harness.Environment.PlantSolution("App.sln");
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
        harness.Environment.PlantSolution("App.sln");
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
        harness.Environment.PlantSolution("App.sln");
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
        harness.Environment.PlantSolution("App.sln");
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
    public async Task ListTools_InspectReportParameter_AdvertisesStringWithEnumValues()
    {
        // Arrange — the second enum on the surface, and therefore the first evidence that the re-injection
        // above generalises past the one parameter it was written for rather than being special-cased.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Act
        IList<McpClientTool> tools = await harness.Client.ListToolsAsync(cancellationToken: Ct);

        // Assert
        McpClientTool inspect = tools.Single(tool => tool.Name == "resharper_inspect");
        JsonElement report = PropertySchema(inspect, "report");
        report.GetProperty("type").GetString().ShouldBe("string");
        EnumValues(report).ShouldBe(["None", "Markdown"]);
    }

    [Fact]
    public async Task ListTools_Inspect_IsStillAdvertisedReadOnly()
    {
        // Arrange — the annotation a client gates auto-approval on. It survives the report parameter on
        // purpose: a run already creates and deletes a temp directory for jb's SARIF, the delta is one file
        // surviving in a directory this server owns, and at the default nothing is written at all.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Act
        IList<McpClientTool> tools = await harness.Client.ListToolsAsync(cancellationToken: Ct);

        // Assert
        McpClientTool inspect = tools.Single(tool => tool.Name == "resharper_inspect");
        inspect.ProtocolTool.Annotations?.ReadOnlyHint.ShouldBe(true);
    }

    [Fact]
    public async Task CallTool_InspectInvalidReport_ReturnsValidValuesErrorAndLogsNothing()
    {
        // Arrange
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        harness.Environment.PlantSolution("App.sln");
        RouteJb(harness.ProcessRunner);

        // Act
        CallToolResult result = await harness.Client.CallToolAsync(
            "resharper_inspect",
            new Dictionary<string, object?> { ["report"] = "Xml" },
            cancellationToken: Ct);

        // Assert — the formats jb offers but this server does not are rejected by name, not silently ignored.
        result.IsError.ShouldBe(true);
        string text = TextOf(result);
        text.ShouldContain("Xml");
        text.ShouldContain("Valid values: None, Markdown");
        harness.Logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListTools_InspectDetailParameter_AdvertisesStringWithEnumValues()
    {
        // Arrange
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Act
        IList<McpClientTool> tools = await harness.Client.ListToolsAsync(cancellationToken: Ct);

        // Assert — the five levels travel in the schema, in ladder order, so the description prose never
        // has to name them and cannot drift from them.
        McpClientTool inspect = tools.Single(tool => tool.Name == "resharper_inspect");
        JsonElement detail = PropertySchema(inspect, "detail");
        detail.GetProperty("type").GetString().ShouldBe("string");
        EnumValues(detail).ShouldBe(["Full", "High", "Medium", "Low", "Minimal"]);
    }

    [Fact]
    public async Task CallTool_InspectInvalidDetail_ReturnsValidValuesErrorAndLogsNothing()
    {
        // Arrange
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        harness.Environment.PlantSolution("App.sln");
        RouteJb(harness.ProcessRunner);

        // Act — "Verbose" is a plausible borrowing from other tools' vocabulary, and is not one of these.
        CallToolResult result = await harness.Client.CallToolAsync(
            "resharper_inspect",
            new Dictionary<string, object?> { ["detail"] = "Verbose" },
            cancellationToken: Ct);

        // Assert
        result.IsError.ShouldBe(true);
        string text = TextOf(result);
        text.ShouldContain("Verbose");
        text.ShouldContain("Valid values: Full, High, Medium, Low, Minimal");
        harness.Logs.Warnings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("file")]
    [InlineData("Files")]
    public async Task CallTool_KeyThatBindsNothing_ReturnsGuardErrorAndLogsNothing(string key)
    {
        // Arrange — "file" is the classic typo of "files", and "Files" its wrong-case twin. The SDK binds each
        // argument by its parameter's exact name, so either would reach cleanup as no files at all and fail
        // inside the binder, where the filter can only log it as unexpected.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Act
        CallToolResult result = await harness.Client.CallToolAsync(
            "resharper_cleanup",
            new Dictionary<string, object?> { [key] = "src/A.cs" },
            cancellationToken: Ct);

        // Assert — the guard names the key and the spelling that binds, ahead of the binder, and logs nothing.
        result.IsError.ShouldBe(true);
        TextOf(result).ShouldBe($"Unknown parameter \"{key}\" on \"resharper_cleanup\". Valid: files, profile, solutionPath.");
        harness.Logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task CallTool_WrongCaseToolName_IsAnsweredWithTheProtocolError()
    {
        // Arrange — the SDK dispatches by exact name too, so this call reaches no tool, and the guard has no
        // schema to check its keys against. A parameter error would send the caller to fix a key on a tool the
        // call can never reach.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);

        // Act
        Task<CallToolResult> call = harness.Client.CallToolAsync(
            "Resharper_Cleanup",
            new Dictionary<string, object?> { ["file"] = "src/A.cs" },
            cancellationToken: Ct).AsTask();

        // Assert — the SDK's JSON-RPC error, not a tool result.
        var error = await Should.ThrowAsync<McpProtocolException>(call);
        error.ErrorCode.ShouldBe(McpErrorCode.InvalidParams);
        error.Message.ShouldContain("Unknown tool");
        error.Message.ShouldNotContain("Unknown parameter");

        // And the server logs nothing: an unknown name is caller input. The one warning is the SDK's own, written
        // by the session handler around every request that ends in a JSON-RPC error, outside this server's filter.
        harness.Logs.Warnings.ShouldHaveSingleItem().ShouldBeTheSdksFailedToolCall<McpProtocolException>();
    }

    [Fact]
    public async Task CallTool_WrongCaseOptionalKey_IsRefusedRatherThanIgnored()
    {
        // Arrange — the silent case: an optional parameter the binder cannot find takes its default, so a
        // "Severity" key would run at Warning and nothing would say the argument went unread.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        harness.Environment.PlantSolution("App.sln");
        List<string>? inspectArguments = null;
        RouteJb(
            harness.ProcessRunner,
            arguments => inspectArguments = [.. arguments],
            Fixtures.ReadSarif("inspect-sample.json"));

        // Act
        CallToolResult result = await harness.Client.CallToolAsync(
            "resharper_inspect",
            new Dictionary<string, object?> { ["Severity"] = "Error" },
            cancellationToken: Ct);

        // Assert — refused before jb ran. The harness leaves the pre-warm off, so the capture can only see
        // this call.
        result.IsError.ShouldBe(true);
        TextOf(result).ShouldStartWith("Unknown parameter \"Severity\" on \"resharper_inspect\". Valid: ");
        inspectArguments.ShouldBeNull();
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
    ///     supplied. Everything succeeds with exit code 0, leaving the cache generation behind that a real
    ///     successful run leaves — see <see cref="CacheHomes.PlantGenerationFromJbRun" />.
    /// </summary>
    private static void RouteJb(
        IProcessRunner processRunner,
        Action<IReadOnlyList<string>>? onCommand = null,
        string? inspectSarif = null)
    {
        processRunner
            .AnyRun()
            .Returns(callInfo =>
            {
                var arguments = callInfo.ArgAt<IReadOnlyList<string>>(1);

                if (JbStubs.IsVersionProbe(arguments)) return JbStubs.VersionProbeAnswer;

                onCommand?.Invoke(arguments);

                if (inspectSarif is not null) JbStubs.WriteSarifIfRequested(arguments, inspectSarif);

                CacheHomes.PlantGenerationFromJbRun(arguments);

                return new ProcessResult(0, string.Empty, string.Empty);
            });
    }
}