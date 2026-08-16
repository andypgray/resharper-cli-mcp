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
///     The MCP tool surface: <c>resharper_inspect</c> (read-only C# inspection), <c>resharper_cleanup</c>
///     (in-place code cleanup), and <c>resharper_reset_cache</c> (drop the solution's analysis cache). Every
///     method validates its inputs and then delegates to a service; they never <c>try/catch</c> —
///     <see cref="GlobalCallToolFilter" /> turns any thrown <see cref="UserErrorException" /> into an error
///     result for the client.
/// </summary>
[McpServerToolType]
internal sealed class ResharperTools(
    ConfigResolver configResolver,
    InspectService inspectService,
    CleanupService cleanupService,
    CacheResetService cacheResetService,
    IEnvironment environment)
{
    internal const string InspectToolName = "resharper_inspect";
    internal const string CleanupToolName = "resharper_cleanup";
    internal const string ResetCacheToolName = "resharper_reset_cache";

    private const string InspectDescription =
        "Run ReSharper static analysis on the solution and return the code issues it finds.";

    private const string CleanupDescription =
        "Run ReSharper code cleanup to reformat and normalize files in place.";

    private const string ResetCacheDescription =
        "Delete this solution's ReSharper analysis cache so the next inspect or cleanup rebuilds it from "
        + "cold. The cure for inspect reporting compilation errors the compiler itself does not: a stale "
        + "index serves those until the cache is dropped. Costs the next call a full cold analysis, so it "
        + "is not routine maintenance.";

    // Descriptions ride the deferred tool schema, which a client fetches only when it is about to call the
    // tool, so a gotcha costs nothing until it is needed. Prefer this over the always-resident server
    // instructions for anything that is per-argument rather than cross-call routing.
    private const string SolutionPathDescription =
        "Path to the .sln/.slnx to run against. Overrides JB_SOLUTION_PATH and working-directory discovery.";

    private const string JoinedPathsNote =
        " An element joining several paths with ; or , is split into separate paths.";

    // Both tools anchor `files` the same way, and used to say so differently: cleanup promised absolute paths
    // worked while inspect said nothing at all. jb's --include takes relative paths only, so an absolute one
    // is translated before it is passed; what it cannot do anything about is a file in no project.
    private const string PathAnchorNote =
        " Each is relative to the solution root, or absolute. jb matches them against the files that belong "
        + "to a project in the solution, so one that is on disk but in no project matches nothing.";

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
            + PathAnchorNote
            + JoinedPathsNote)]
        string[]? files = null,
        [Description(
            "Minimum severity to report. Error is ReSharper's compilation-error level, not a tier of "
            + "high-priority warnings; raising to it usually reports nothing.")]
        InspectSeverity severity = InspectSeverity.Warning,
        [Description(SolutionPathDescription)] string? solutionPath = null,
        CancellationToken cancellationToken = default)
    {
        ResolvedConfig config = await configResolver.ResolveAsync(solutionPath, cancellationToken);

        // An entry joining several paths would reach jb as one --include pattern that matches nothing, and
        // this tool would report "No issues found." for a scan that never looked at the files asked for.
        IReadOnlyList<string>? scope = FilePathList.Split(files, config.SolutionDirectory);

        IReadOnlyList<InspectIssue> issues = await inspectService.RunAsync(config, scope, severity, cancellationToken);

        // Two independent preambles, concatenated: configuration that was dropped before the run, then how to
        // read compilation errors in what came back. Either can be empty, and both ride outside the reduction
        // ladder.
        string banner = ConfigWarningBanner.ForInspect(config.Warnings)
                        + CompilationErrorNote.For(issues, config.CacheHome);

        return RenderWithBanner(
            banner,
            issues,
            IssueMarkdownFormatter.Format,
            IssueMarkdownFormatter.DescribeReduction);
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
            "File paths to clean up."
            + PathAnchorNote
            + " Wildcards are allowed and expanded by jb; a non-wildcard path that does not exist fails the "
            + "whole call before anything is rewritten."
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
        IReadOnlyList<string> paths = FilePathList.Split(files, config.SolutionDirectory);

        CleanupOutcome outcome = await cleanupService.RunAsync(config, paths, profile, cancellationToken);

        return RenderWithBanner(
            ConfigWarningBanner.ForCleanup(config.Warnings),
            outcome,
            CleanupSummaryFormatter.Format,
            CleanupSummaryFormatter.DescribeReduction);
    }

    [McpServerTool(
        Name = ResetCacheToolName,
        Title = "ReSharper Reset Cache",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(ResetCacheDescription)]
    public async Task<string> ResetCacheAsync(
        [Description(SolutionPathDescription)] string? solutionPath = null,
        CancellationToken cancellationToken = default)
    {
        ResolvedConfig config = await configResolver.ResolveAsync(solutionPath, cancellationToken);

        CacheResetOutcome outcome = await cacheResetService.RunAsync(config, cancellationToken);

        // No banner and no reduction ladder, unlike the two tools above. The config warnings both describe
        // settings that shape a jb run, and this call makes none; the ladder is answered on the formatter.
        return CacheResetFormatter.Format(outcome);
    }

    /// <summary>
    ///     The one response-shaping tail both analysis tools share: render <paramref name="data" /> at the highest
    ///     <see cref="DetailLevel" /> that fits the client's output budget (the GlobalCallToolFilter's
    ///     truncator is the final backstop), led by <paramref name="banner" />. The banner is charged to the
    ///     budget <em>before</em> rendering, which is what puts it outside the reduction ladder: it survives
    ///     every step down to Minimal without making truncation any likelier. Inspect must not let an empty
    ///     result read as "nothing to report" when settings were dropped; cleanup must report the profile the
    ///     files were <em>not</em> cleaned with once they are already rewritten.
    /// </summary>
    private string RenderWithBanner<T>(
        string banner,
        T data,
        Func<T, DetailLevel, string> format,
        Func<DetailLevel, string> describeReduction)
    {
        int maxChars = ResponseTruncator.ComputeMaxChars(environment);
        string body = ProgressiveRenderer.Render(
            data,
            format,
            ResponseTruncator.BudgetForBody(maxChars, banner),
            describeReduction);

        return banner + body;
    }

    /// <summary>
    ///     The domain remedy <c>ResponseTruncator</c> closes a hard-truncated response with, keyed by tool:
    ///     inspect points at narrowing the next scan, cleanup at the fact that shrinking the report did not
    ///     shrink the cleanup. Lives with the tools so the generic backstop needs no per-tool knowledge and
    ///     a new tool contributes its hint here.
    /// </summary>
    internal static string TruncationHintFor(string? toolName)
    {
        return toolName switch
        {
            InspectToolName => IssueMarkdownFormatter.NarrowingHint,
            CleanupToolName => CleanupSummaryFormatter.CleanupRanInFull,
            ResetCacheToolName => CacheResetFormatter.ResetRanInFull,
            _ => ""
        };
    }
}