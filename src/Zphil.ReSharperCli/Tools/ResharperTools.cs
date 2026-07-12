using System.ComponentModel;
using ModelContextProtocol.Server;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Infrastructure;
using Zphil.ReSharperCli.Pipeline;
using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Tools;

/// <summary>
///     The MCP tool surface: <c>resharper_inspect</c> (read-only C# inspection) and
///     <c>resharper_cleanup</c> (in-place code cleanup). Both methods validate their inputs and then
///     delegate to a service; they never <c>try/catch</c> — <see cref="GlobalCallToolFilter" />
///     turns any thrown <see cref="UserErrorException" /> into an error result for the client.
/// </summary>
[McpServerToolType]
internal sealed class ResharperTools(
    ConfigResolver configResolver,
    InspectService inspectService,
    CleanupService cleanupService,
    IEnvironment environment)
{
    internal const string InspectToolName = "resharper_inspect";
    internal const string CleanupToolName = "resharper_cleanup";

    private const string InspectDescription =
        "Run ReSharper static analysis on the solution and return the code issues it finds.";

    private const string CleanupDescription =
        "Run ReSharper code cleanup to reformat and normalize files in place.";

    private const string SolutionPathDescription = "Path to the .sln/.slnx to analyze.";

    [McpServerTool(
        Name = InspectToolName,
        Title = "ReSharper Inspect Code",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(InspectDescription)]
    public async Task<string> InspectAsync(
        [Description("Ant-style globs scoping the analysis to specific files.")]
        string[]? files = null,
        [Description("Minimum severity to report.")]
        InspectSeverity severity = InspectSeverity.Warning,
        [Description(SolutionPathDescription)] string? solutionPath = null,
        CancellationToken cancellationToken = default)
    {
        // Validation happens at the binding layer (EnumValidationConverterFactory); here we only
        // map the enum to jb's CLI token. --severity is a CLI-flag concern, so InspectService stays
        // string-based and its pinned argument-order tests are untouched.
        string cliSeverity = severity.ToString().ToUpperInvariant();

        ResolvedConfig config = await configResolver.ResolveAsync(solutionPath, cancellationToken);
        var issues = await inspectService.RunAsync(config, files, cliSeverity, cancellationToken);

        return IssueMarkdownFormatter.Format(issues);
    }

    [McpServerTool(
        Name = CleanupToolName,
        Title = "ReSharper Cleanup Code",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(CleanupDescription)]
    public async Task<string> CleanupAsync(
        [Description("File paths to clean up, relative to the solution root or absolute.")]
        string[] files,
        [Description("ReSharper cleanup profile name.")]
        string profile = CleanupService.DefaultProfile,
        [Description(SolutionPathDescription)] string? solutionPath = null,
        CancellationToken cancellationToken = default)
    {
        if (files is null || files.Length == 0) throw new UserErrorException("At least one file must be specified.");

        ResolvedConfig config = await configResolver.ResolveAsync(solutionPath, cancellationToken);
        CleanupOutcome outcome = await cleanupService.RunAsync(config, files, profile, cancellationToken);

        // Render at the highest DetailLevel that fits the client's output budget (a small batch fits at
        // Full, an unchanged plain per-file list); the GlobalCallToolFilter's truncator is the final backstop.
        int maxChars = ResponseTruncator.ComputeMaxChars(environment.GetVariable("MAX_MCP_OUTPUT_TOKENS"));
        return ProgressiveRenderer.Render(outcome, CleanupSummaryFormatter.Format, maxChars);
    }
}