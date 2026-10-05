using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Pipeline;

/// <summary>
///     Drives a real MCP client against the server over in-memory pipes to prove the input-coercion
///     pipeline end to end.
/// </summary>
public sealed class CoercionIntegrationTests(AdvertisedToolsFixture advertised)
    : IClassFixture<AdvertisedToolsFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Each enum parameter inspect takes, and the values its schema must list in declaration order.</summary>
    public static TheoryData<string, string[]> InspectEnumParameters =>
        new()
        {
            { "severity", ["Suggestion", "Warning", "Error"] },
            // A second enum, which is what shows the re-injection generalises rather than being special-cased to
            // severity.
            { "report", ["None", "Markdown"] },
            // The five levels in ladder order, so the description prose never has to name them.
            { "detail", ["Full", "High", "Medium", "Low", "Minimal"] }
        };

    [Fact]
    public void ListTools_FilesParameter_AdvertisesArrayOfStringsRequiredByCleanupAndNotByInspect()
    {
        // Arrange
        McpClientTool cleanup = advertised.Tools.Single(tool => tool.Name == "resharper_cleanup");
        McpClientTool inspect = advertised.Tools.Single(tool => tool.Name == "resharper_inspect");

        // Act
        JsonElement files = cleanup.PropertySchema("files");

        // Assert — the schema-erasure guard: StringArrayCoercerFactory would collapse this to {} without
        // the re-injection step. type/items must survive, and files must stay schema-required on cleanup
        // while inspect's stays optional.
        files.GetProperty("type").GetString().ShouldBe("array");
        files.GetProperty("items").GetProperty("type").GetString().ShouldBe("string");
        cleanup.RequiredProperties().ShouldContain("files");
        inspect.RequiredProperties().ShouldNotContain("files");
    }

    [Theory]
    [MemberData(nameof(InspectEnumParameters))]
    public void ListTools_InspectEnumParameter_AdvertisesStringWithItsValues(string parameter, string[] values)
    {
        // Arrange
        McpClientTool inspect = advertised.Tools.Single(tool => tool.Name == "resharper_inspect");

        // Act
        JsonElement schema = inspect.PropertySchema(parameter);

        // Assert — EnumValidationConverterFactory erases the enum's shape; re-injection restores both the
        // string type AND the value list, so the allowed values travel in the schema itself. This is the
        // guard that lets the description prose stay free of the enum names.
        schema.GetProperty("type").GetString().ShouldBe("string");
        inspect.EnumValues(parameter).ShouldBe(values);
    }

    [Fact]
    public void ListTools_ScalarStringParameters_AdvertiseStringType()
    {
        // Arrange — the scalar-string reinjection is load-bearing: this project's exporter erases every
        // string?/string parameter to a bare {} under StringCoercerFactory. Assert a representative one
        // on each tool advertises a plain "string" (not {}, and not a ["string","null"] union).
        McpClientTool inspect = advertised.Tools.Single(tool => tool.Name == "resharper_inspect");
        McpClientTool cleanup = advertised.Tools.Single(tool => tool.Name == "resharper_cleanup");

        // Act — inspect.solutionPath (nullable, no default) and cleanup.profile (a real default).
        JsonElement solutionPath = inspect.PropertySchema("solutionPath");
        JsonElement profile = cleanup.PropertySchema("profile");

        // Assert
        solutionPath.GetProperty("type").GetString().ShouldBe("string");
        profile.GetProperty("type").GetString().ShouldBe("string");
    }

    [Fact]
    public async Task CallTool_CleanupFilesAsBareString_CoercesToSingleFile()
    {
        // Arrange — a bare string where files : string[] is advertised. Must be single-coerced.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        harness.Environment.PlantSolution("App.sln");
        SolutionFiles.Plant(harness.Environment.CurrentDirectory, "src/A.cs");
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
        SolutionFiles.Plant(harness.Environment.CurrentDirectory, "src/A.cs");
        SolutionFiles.Plant(harness.Environment.CurrentDirectory, "src/B.cs");
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

    [Theory]
    [InlineData("severity", "HIGH", "Suggestion, Warning, Error")]
    // The formats jb offers but this server does not are rejected by name, not silently ignored.
    [InlineData("report", "Xml", "None, Markdown")]
    // A plausible borrowing from other tools' vocabulary, and not one of these.
    [InlineData("detail", "Verbose", "Full, High, Medium, Low, Minimal")]
    public async Task CallTool_InspectInvalidEnumValue_ReturnsValidValuesErrorAndLogsNothing(
        string parameter,
        string invalidValue,
        string validValues)
    {
        // Arrange — the coercer throws inside the SDK argument binder, which wraps it in JsonException(s).
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(Ct);
        harness.Environment.PlantSolution("App.sln");
        RouteJb(harness.ProcessRunner);

        // Act
        CallToolResult result = await harness.Client.CallToolAsync(
            "resharper_inspect",
            new Dictionary<string, object?> { [parameter] = invalidValue },
            cancellationToken: Ct);

        // Assert — the friendly valid-values message surfaced, and FindUserError kept it out of the log.
        result.IsError.ShouldBe(true);
        string text = result.Text();
        text.ShouldContain(invalidValue);
        text.ShouldContain($"Valid values: {validValues}");
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
        result.Text().ShouldBe($"Unknown parameter \"{key}\" on \"resharper_cleanup\". Valid: files, profile, solutionPath.");
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
        result.Text().ShouldStartWith("Unknown parameter \"Severity\" on \"resharper_inspect\". Valid: ");
        inspectArguments.ShouldBeNull();
        harness.Logs.Warnings.ShouldBeEmpty();
    }

    /// <summary>
    ///     Routes the process-runner substitute by jb sub-command: the version probe succeeds, and the
    ///     inspectcode/cleanupcode run is handed to <paramref name="onCommand" /> for argument capture, then
    ///     answered by <see cref="JbStubs.Succeed" /> with <paramref name="inspectSarif" /> as its report.
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
                IReadOnlyList<string> arguments = callInfo.Arguments();

                if (JbStubs.IsVersionProbe(arguments)) return JbStubs.VersionProbeAnswer;

                onCommand?.Invoke(arguments);

                return JbStubs.Succeed(arguments, inspectSarif);
            });
    }
}