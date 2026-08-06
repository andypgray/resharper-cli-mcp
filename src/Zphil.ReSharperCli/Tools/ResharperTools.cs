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

    private const string JoinedPathsNote =
        " An element joining several paths with ; or , is split into separate paths.";

    [McpServerTool(
        Name = InspectToolName,
        Title = "ReSharper Inspect Code",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(InspectDescription)]
    public async Task<string> InspectAsync(
        [Description(
            "Ant-style globs scoping the analysis to specific files, for example src/**/*.cs."
            + JoinedPathsNote)]
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

        // An entry joining several paths would reach jb as one --include pattern that matches nothing, and
        // this tool would report "No issues found." for a scan that never looked at the files asked for.
        var scope = FilePathList.Split(files, config.SolutionDirectory);

        // Widened from the service's List<T> so ProgressiveRenderer's T infers to the formatter's own
        // parameter type.
        IReadOnlyList<InspectIssue> issues =
            await inspectService.RunAsync(config, scope, cliSeverity, cancellationToken);

        // Render at the highest DetailLevel that fits the client's output budget: a scoped scan fits at
        // Full (today's per-issue listing), while a solution-wide run collapses repeated rules rather than
        // being chopped mid-list. The GlobalCallToolFilter's truncator is the final backstop. The banner
        // leads and is charged to the budget, so it outlives every reduction step — an empty result is
        // exactly where "no settings were applied" must not be mistaken for "nothing to report".
        string banner = ConfigWarningBanner.ForInspect(config.Warnings);
        int maxChars = ResponseTruncator.ComputeMaxChars(environment.GetVariable("MAX_MCP_OUTPUT_TOKENS"));
        string body = ProgressiveRenderer.Render(
            issues,
            IssueMarkdownFormatter.Format,
            ResponseTruncator.BudgetForBody(maxChars, banner),
            IssueMarkdownFormatter.DescribeReduction);

        return banner + body;
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
            + "is rewritten."
            + JoinedPathsNote)]
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

        // Splitting an entry that joins several paths runs after the checks above, so a malformed list still
        // reports the caller's own indices, and before the service, whose contract is an already-validated
        // list. An entry that names a real file is never reinterpreted, so this can only rescue a call that
        // was going to fail — the bar a tool that rewrites files has to clear.
        var paths = FilePathList.Split(files, config.SolutionDirectory);

        // An unspecified profile resolves to the solution's own declared profile before the built-in
        // default, so a repo that narrowed its cleanup gets that narrowing on every call — including the
        // calls of an agent that does not know the profile exists. A blank argument reads as unspecified,
        // matching how a blank declared profile reads.
        string resolvedProfile = CleanupProfileReader.Normalize(profile)
                                 ?? config.CleanupProfile
                                 ?? CleanupService.DefaultProfile;
        CleanupOutcome outcome = await cleanupService.RunAsync(config, paths, resolvedProfile, cancellationToken);

        // Render at the highest DetailLevel that fits the client's output budget (a small batch fits at
        // Full, an unchanged plain per-file list); the GlobalCallToolFilter's truncator is the final backstop.
        // The banner leads and is charged to the budget so it survives every reduction step: it reports the
        // profile the files were *not* cleaned with, and the files are already rewritten by this point.
        string banner = ConfigWarningBanner.ForCleanup(config.Warnings);
        int maxChars = ResponseTruncator.ComputeMaxChars(environment.GetVariable("MAX_MCP_OUTPUT_TOKENS"));
        string body = ProgressiveRenderer.Render(
            outcome,
            CleanupSummaryFormatter.Format,
            ResponseTruncator.BudgetForBody(maxChars, banner),
            CleanupSummaryFormatter.DescribeReduction);

        return banner + body;
    }
}