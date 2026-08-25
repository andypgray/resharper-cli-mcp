using System.ComponentModel;
using Microsoft.Extensions.Logging;
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
    InspectReportWriter reportWriter,
    IEnvironment environment,
    ILogger<ResharperTools> logger)
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

    /// <remarks>
    ///     Still annotated read-only with <paramref name="report" /> on the surface, and deliberately. A run
    ///     already creates and deletes a temp directory for <c>jb</c>'s SARIF; the delta here is that one file
    ///     survives, in a directory this server owns and names in its response. Nothing in the workspace, the
    ///     solution, or the cache is touched, and at the default <see cref="InspectReport.None" /> nothing is
    ///     written at all.
    /// </remarks>
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
        [Description(
            "Write the complete itemised findings to a file, and name it in the response. Markdown lists "
            + "every issue with its own message, which is what the response listing collapses once a "
            + "solution-wide run exceeds the output budget. The file lands in a directory this server owns "
            + "and is pruned after 7 days; the response carries the summary either way.")]
        InspectReport report = InspectReport.None,
        [Description(
            "Cap on the detail the response carries: Full, the default, lists every issue on its own line, "
            + "and Minimal is one line of totals. Rendering starts at this level and never goes above it, "
            + "but still steps below it when the result does not fit the output budget. The DETAIL REDUCED "
            + "note says which of the two happened. Response shaping only: the same analysis runs whatever "
            + "the level, so a lower level does not make a call finish sooner. Pair detail=Minimal with "
            + "report=Markdown for a cheap verdict in the response and every finding in the file.")]
        InspectDetail detail = InspectDetail.Full,
        [Description(SolutionPathDescription)] string? solutionPath = null,
        CancellationToken cancellationToken = default)
    {
        // First, ahead of the run: this is the one argument whose translation can fail, and a failure here
        // after jb has spent minutes would have cost those minutes to learn nothing.
        DetailLevel cap = CapFor(detail);

        ResolvedConfig config = await configResolver.ResolveAsync(solutionPath, cancellationToken);

        // An entry joining several paths would reach jb as one --include pattern that matches nothing, and
        // this tool would report "No issues found." for a scan that never looked at the files asked for.
        IReadOnlyList<string>? scope = FilePathList.Split(files, config.SolutionDirectory);

        IReadOnlyList<InspectIssue> issues = await inspectService.RunAsync(config, scope, severity, cancellationToken);

        // The Full rendering is both the report file's body and the ladder's first attempt at the default
        // detail; render it at most once and hand both the same string.
        string? full = null;

        string RenderFull()
        {
            return full ??= IssueMarkdownFormatter.Format(issues, DetailLevel.Full);
        }

        InspectReportOutcome? written = WriteReport(report, RenderFull, config, severity, scope);

        // Three independent preambles, concatenated: configuration that was dropped before the run, how to
        // read compilation errors in what came back, and where the full listing went. Each can be empty, and
        // all ride outside the reduction ladder.
        string banner = ConfigWarningBanner.ForInspect(config.Warnings)
                        + CompilationErrorNote.For(issues, config.CacheHome)
                        + InspectReportNote.For(written, issues.Count);

        // The level asked for suppresses the narrowing remedy only when the rendering actually settles
        // there: below the cap, the budget forced the step and the remedy is as useful as it ever was.
        return RenderWithBanner(
            banner,
            issues,
            (data, level) => level == DetailLevel.Full ? RenderFull() : IssueMarkdownFormatter.Format(data, level),
            level => IssueMarkdownFormatter.DescribeReduction(level, written is { Failure: null }, level == cap),
            cap);
    }

    /// <summary>
    ///     The tool-facing <see cref="InspectDetail" /> as the <see cref="DetailLevel" /> the ladder starts
    ///     from. Spelt out member by member rather than cast or round-tripped through
    ///     <see cref="Enum.Parse{TEnum}(string)" />: either of those would keep compiling if the two enums
    ///     diverged and quietly resolve the wrong level, which is the failure keeping them separate exists
    ///     to prevent.
    /// </summary>
    private static DetailLevel CapFor(InspectDetail detail)
    {
        return detail switch
        {
            InspectDetail.Full => DetailLevel.Full,
            InspectDetail.High => DetailLevel.High,
            InspectDetail.Medium => DetailLevel.Medium,
            InspectDetail.Low => DetailLevel.Low,
            InspectDetail.Minimal => DetailLevel.Minimal,
            _ => throw new ArgumentOutOfRangeException(nameof(detail), detail, "Unmapped inspect detail level.")
        };
    }

    /// <summary>
    ///     The report file, or <see langword="null" /> when none was asked for. Rendered here rather than in
    ///     <see cref="InspectReportWriter" /> so that class stays about files and <c>Formatting/</c> keeps
    ///     owning what a rendering looks like. Written even when there are no issues: "a report was asked for,
    ///     so the response names a file that exists" is a contract a caller can script against, and one that
    ///     sometimes yields no file is not. The body comes in as <paramref name="renderFull" /> so the same
    ///     rendering serves the response ladder rather than being produced twice.
    /// </summary>
    private InspectReportOutcome? WriteReport(
        InspectReport report,
        Func<string> renderFull,
        ResolvedConfig config,
        InspectSeverity severity,
        IReadOnlyList<string>? scope)
    {
        if (report == InspectReport.None) return null;

        string document = InspectReportDocument.Compose(
            renderFull(),
            config.SolutionPath,
            severity.ToJbToken(),
            scope,
            DateTimeOffset.UtcNow);

        return reportWriter.WriteMarkdown(document, config.SolutionPath);
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

        // No detail parameter here, though the plumbing is shared: what this ladder reduces is a status
        // line per file, over the list the caller supplied and can therefore already shorten itself.
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
    ///     <paramref name="startLevel" /> is where that ladder begins — inspect's <c>detail</c> cap, and for
    ///     cleanup the default, which is the whole ladder.
    /// </summary>
    private string RenderWithBanner<T>(
        string banner,
        T data,
        Func<T, DetailLevel, string> format,
        Func<DetailLevel, string> describeReduction,
        DetailLevel startLevel = DetailLevel.Full)
    {
        int maxChars = ResponseTruncator.ComputeMaxChars(environment);
        ProgressiveRendering rendering = ProgressiveRenderer.Render(
            data,
            format,
            ResponseTruncator.BudgetForBody(maxChars, banner),
            describeReduction,
            startLevel);

        // Debug: the level a response settled at is shaping, not caching, and it is already stated in the
        // response an agent received. What it buys the log is the answer to whether the ladder has ever had to
        // step down on real results at all — which, from the outside, a mechanism that never fires and one
        // that is broken cannot be told apart on.
        logger.LogDebug(
            "Rendered {ResultType} at {DetailLevel} in {BodyChars} of {MaxChars} characters, {BannerChars} of them banner",
            typeof(T).Name,
            rendering.Level,
            rendering.Text.Length,
            maxChars,
            banner.Length);

        return banner + rendering.Text;
    }

    /// <summary>
    ///     The domain remedy <c>ResponseTruncator</c> closes a hard-truncated response with, keyed by tool:
    ///     inspect points at narrowing the next scan and at the report file, cleanup at the fact that
    ///     shrinking the report did not shrink the cleanup. Lives with the tools so the generic backstop needs
    ///     no per-tool knowledge and a new tool contributes its hint here.
    /// </summary>
    /// <remarks>
    ///     Keyed by tool name and nothing else, so it cannot know a report was already written and will
    ///     suggest one anyway. Harmless: the note naming the file is a prefix and survives the cut, so the
    ///     path is still above the footer that repeats the offer.
    /// </remarks>
    internal static string TruncationHintFor(string? toolName)
    {
        return toolName switch
        {
            InspectToolName => IssueMarkdownFormatter.TruncationRemedy,
            CleanupToolName => CleanupSummaryFormatter.CleanupRanInFull,
            ResetCacheToolName => CacheResetFormatter.ResetRanInFull,
            _ => ""
        };
    }
}