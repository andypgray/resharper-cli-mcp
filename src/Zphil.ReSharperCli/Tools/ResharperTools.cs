using System.ComponentModel;
using ModelContextProtocol.Server;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Infrastructure;
using Zphil.ReSharperCli.Pipeline;
using Zphil.ReSharperCli.Sarif;
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

    // Descriptions ride the deferred tool schema, which a client fetches only when it is about to call the
    // tool, so a gotcha costs nothing until it is needed. Prefer this over the always-resident server
    // instructions for anything that is per-argument rather than cross-call routing.
    private const string SolutionPathDescription =
        "Path to the .sln/.slnx to run against. Overrides JB_SOLUTION_PATH and working-directory discovery.";

    [McpServerTool(
        Name = InspectToolName,
        Title = "ReSharper Inspect Code",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(InspectDescription)]
    public async Task<string> InspectAsync(
        [Description("Ant-style globs scoping the analysis to specific files, for example src/**/*.cs.")]
        string[]? files = null,
        [Description(
            "Minimum severity to report. Error is ReSharper's compilation-error level, not a tier of "
            + "high-priority warnings; raising to it usually reports nothing.")]
        InspectSeverity severity = InspectSeverity.Warning,
        [Description(SolutionPathDescription)] string? solutionPath = null,
        CancellationToken cancellationToken = default)
    {
        // Validation happens at the binding layer (EnumValidationConverterFactory); here we only
        // map the enum to jb's CLI token. --severity is a CLI-flag concern, so InspectService stays
        // string-based and its pinned argument-order tests are untouched.
        string cliSeverity = severity.ToString().ToUpperInvariant();

        ResolvedConfig config = await configResolver.ResolveAsync(solutionPath, cancellationToken);

        // Widened from the service's List<T> so ProgressiveRenderer's T infers to the formatter's own
        // parameter type.
        IReadOnlyList<InspectIssue> issues =
            await inspectService.RunAsync(config, files, cliSeverity, cancellationToken);

        // Render at the highest DetailLevel that fits the client's output budget: a scoped scan fits at
        // Full (today's per-issue listing), while a solution-wide run collapses repeated rules rather than
        // being chopped mid-list. The GlobalCallToolFilter's truncator is the final backstop.
        int maxChars = ResponseTruncator.ComputeMaxChars(environment.GetVariable("MAX_MCP_OUTPUT_TOKENS"));
        return ProgressiveRenderer.Render(
            issues, IssueMarkdownFormatter.Format, maxChars, IssueMarkdownFormatter.DescribeReduction);
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
        [Description(
            "File paths to clean up, relative to the solution root or absolute. Wildcards are allowed and "
            + "expanded by jb; a non-wildcard path that does not exist fails the whole call before anything "
            + "is rewritten.")]
        string[] files,
        [Description("ReSharper cleanup profile name. Defaults to the profile the solution declares, else full cleanup.")]
        string? profile = null,
        [Description(SolutionPathDescription)] string? solutionPath = null,
        CancellationToken cancellationToken = default)
    {
        if (files is null || files.Length == 0) throw new UserErrorException("At least one file must be specified.");

        // A blank entry names no file, and every downstream path resolution would throw on it. This tool
        // rewrites what it is given, so a malformed list fails naming the offending position rather than
        // quietly cleaning up whatever the rest of the list happens to name.
        int blankIndex = Array.FindIndex(files, string.IsNullOrWhiteSpace);
        if (blankIndex >= 0)
            throw new UserErrorException($"File paths must not be blank (files[{blankIndex}] is empty).");

        ResolvedConfig config = await configResolver.ResolveAsync(solutionPath, cancellationToken);

        // An unspecified profile resolves to the solution's own declared profile before the built-in
        // default, so a repo that narrowed its cleanup gets that narrowing on every call — including the
        // calls of an agent that does not know the profile exists. A blank argument reads as unspecified,
        // matching how a blank declared profile reads.
        string resolvedProfile = CleanupProfileReader.Normalize(profile)
                                 ?? config.CleanupProfile
                                 ?? CleanupService.DefaultProfile;
        CleanupOutcome outcome = await cleanupService.RunAsync(config, files, resolvedProfile, cancellationToken);

        // Render at the highest DetailLevel that fits the client's output budget (a small batch fits at
        // Full, an unchanged plain per-file list); the GlobalCallToolFilter's truncator is the final backstop.
        int maxChars = ResponseTruncator.ComputeMaxChars(environment.GetVariable("MAX_MCP_OUTPUT_TOKENS"));
        return ProgressiveRenderer.Render(
            outcome, CleanupSummaryFormatter.Format, maxChars, CleanupSummaryFormatter.DescribeReduction);
    }
}