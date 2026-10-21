using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Infrastructure;
using Zphil.ReSharperCli.Pipeline;
using Zphil.ReSharperCli.Sarif;
using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Tools;

/// <summary>
///     Defines the MCP tool surface.
/// </summary>
/// <remarks>
///     Every method validates its inputs and then delegates to a service; they never <c>try/catch</c> —
///     <see cref="GlobalCallToolFilter" /> turns any thrown <see cref="UserErrorException" /> into an error
///     result for the client.
/// </remarks>
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
        "Run ReSharper code cleanup to reformat and normalize files in place. Each call analyses the whole "
        + "solution whatever the file count, so make one call per task with every modified file in it.";

    private const string ResetCacheDescription =
        "Delete this solution's ReSharper analysis cache so the next inspect or cleanup rebuilds it from "
        + "cold. The cure for inspect reporting compilation errors a successful build does not: a stale "
        + "index serves those until the cache is dropped. Build first, because on a checkout that was never "
        + "built or restored the errors are real and a reset only adds a cold analysis to the build it "
        + "still needs. Costs the next call a full cold analysis, so it is not routine maintenance. A deleted "
        + "checkout's cache outlives it: pass its old solution path as solutionPath to reclaim it.";

    // Descriptions ride the deferred tool schema, fetched as the call is made, so a per-argument gotcha, a cost
    // that decides whether to call at all, or a gate on a destructive follow-up goes here with its cure stated
    // inline, not in the always-resident server instructions.
    private const string SolutionPathDescription =
        "Path to the .sln/.slnx to run against. Overrides JB_SOLUTION_PATH and working-directory discovery.";

    private const string JoinedPathsNote =
        " An element joining several paths with ; or , is split into separate paths.";

    // Every tool that takes `files` anchors it the same way, so they share this text. jb's --include takes
    // relative paths only, so an absolute one is translated before it is passed; what that cannot fix is a
    // file in no project.
    private const string PathAnchorNote =
        " Each is relative to the solution root, or absolute. jb matches them against the files that belong "
        + "to a project in the solution, so one that is on disk but in no project matches nothing, which "
        + "nothing here can detect.";

    /// <remarks>
    ///     Annotated read-only even with <paramref name="report" /> on the surface, because a report is one file
    ///     in a directory this server owns and names in its response, and nothing in the workspace, the solution
    ///     or the cache is touched.
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
            "Ant-style globs narrowing the findings to matching files, for example src/**/*.cs. The "
            + "analysis stays solution-wide, so a scoped run is no faster."
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
        RequestContext<CallToolRequestParams>? context = null,
        CancellationToken cancellationToken = default)
    {
        // Disposed after the return expression is built and before the SDK writes the result frame, which is
        // what orders the last notification ahead of the result.
        await using ProgressSink? progress = ProgressSink.For(context, logger);
        Action<string>? onProgress = progress is null ? null : progress.Send;

        // Ahead of the run: this is the one argument whose translation can fail, and a failure here after jb
        // has spent minutes would have cost those minutes to learn nothing.
        DetailLevel cap = CapFor(detail);

        ResolvedConfig config = await configResolver.ResolveAsync(solutionPath, cancellationToken);

        // An entry joining several paths would reach jb as one --include pattern that matches nothing.
        IReadOnlyList<string>? scope = FilePathList.Split(files, config.SolutionDirectory);

        // Classified here, before the run and by the rule cleanup applies before its own, rather than by
        // the note that reports them: a formatter reads nothing from disk.
        List<string> missingScope = scope is null ? [] : FilePathList.FindMissing(scope, config.SolutionDirectory);

        IReadOnlyList<InspectIssue> issues = await inspectService.RunAsync(
            config, scope, severity, cancellationToken, onProgress);

        // The Full rendering is both the report file's body and the ladder's first attempt at the default
        // detail; render it at most once and hand both the same string.
        string? full = null;

        string RenderFull()
        {
            return full ??= IssueMarkdownFormatter.Format(issues, DetailLevel.Full);
        }

        InspectReportOutcome? written = WriteReport(report, RenderFull, config, severity, scope);

        // Concatenated in reading order, from what was dropped before the run to where the full listing went.
        // Each can be empty.
        string banner = ConfigWarningBanner.ForInspect(config.Warnings)
                        + InspectScopeNote.For(missingScope, scope?.Count ?? 0, config.SolutionDirectory)
                        + CompilationErrorNote.For(issues, config.CacheHome)
                        + InspectReportNote.For(written, issues.Count);

        // The level asked for suppresses the narrowing remedy only when the rendering settles there: below
        // the cap, the budget forced the step and the remedy is as useful as it ever was.
        return RenderWithBanner(
            banner,
            issues,
            (data, level) => level == DetailLevel.Full ? RenderFull() : IssueMarkdownFormatter.Format(data, level),
            level => IssueMarkdownFormatter.DescribeReduction(
                level, issues.Count, written is { Failure: null }, level == cap),
            cap);
    }

    /// <summary>
    ///     Maps the tool-facing <see cref="InspectDetail" /> to the <see cref="DetailLevel" /> the ladder starts
    ///     from.
    /// </summary>
    /// <remarks>
    ///     Spelt out member by member rather than cast or round-tripped through
    ///     <see cref="Enum.Parse{TEnum}(string)" />: either of those would keep compiling if the two enums
    ///     diverged and silently resolve the wrong level, which is the failure keeping them separate exists to
    ///     prevent.
    /// </remarks>
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
    ///     Writes the report file, or returns <see langword="null" /> when none was asked for.
    /// </summary>
    /// <remarks>
    ///     Composed here rather than in <see cref="InspectReportWriter" />, so that class stays about files and
    ///     <c>Formatting/</c> keeps owning what a rendering looks like. Written even when there are no issues, so
    ///     a report asked for always names a file that exists.
    /// </remarks>
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
        RequestContext<CallToolRequestParams>? context = null,
        CancellationToken cancellationToken = default)
    {
        await using ProgressSink? progress = ProgressSink.For(context, logger);
        Action<string>? onProgress = progress is null ? null : progress.Send;

        if (files is null || files.Length == 0) throw new UserErrorException("At least one file must be specified.");

        // A blank entry names no file, and every downstream path resolution would throw on it. This tool
        // rewrites what it is given, so a malformed list fails naming the offending position rather than
        // silently cleaning up whatever the rest of the list happens to name.
        int blankIndex = Array.FindIndex(files, string.IsNullOrWhiteSpace);
        if (blankIndex >= 0)
            throw new UserErrorException($"File paths must not be blank (files[{blankIndex}] is empty).");

        ResolvedConfig config = await configResolver.ResolveAsync(solutionPath, cancellationToken);

        // Splitting an entry that joins several paths runs after the checks above, so a malformed list still
        // reports the caller's own indices, and before the service, whose contract is an already-validated
        // list. An entry that names a real file is never reinterpreted, so this can only rescue a call that
        // was going to fail — the bar a tool that rewrites files has to clear.
        IReadOnlyList<string> paths = FilePathList.Split(files, config.SolutionDirectory);

        CleanupOutcome outcome = await cleanupService.RunAsync(
            config, paths, profile, cancellationToken, onProgress);

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
        RequestContext<CallToolRequestParams>? context = null,
        CancellationToken cancellationToken = default)
    {
        await using ProgressSink? progress = ProgressSink.For(context, logger);
        Action<string>? onProgress = progress is null ? null : progress.Send;

        // The one resolution that accepts a solution path naming no file, because a cache generation is
        // addressed by the hash of the path rather than by the file: that is what lets a deleted checkout's
        // cache be reclaimed.
        ResolvedConfig config = await configResolver.ResolveForCacheResetAsync(solutionPath, cancellationToken);

        // The one thing this tool can be slow at is queueing behind another session's jb, and it spawns no
        // process of its own to stream — so that wait is the whole of what there is to report.
        CacheResetOutcome outcome = await cacheResetService.RunAsync(
            config, cancellationToken, onProgress);

        // No banner and no reduction ladder. The config warnings describe settings that shape a jb run, and
        // this call makes none; CacheResetFormatter says why it has no ladder.
        return CacheResetFormatter.Format(outcome);
    }

    /// <summary>
    ///     Renders <paramref name="data" /> at the highest <see cref="DetailLevel" /> that fits the client's
    ///     output budget, led by <paramref name="banner" />, which is charged to the budget first.
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

        // Debug: the level a response settled at is shaping, not caching, and the response already states it
        // to the agent that received it. The log is the only record of whether the ladder ever steps down on
        // real results: from outside, a ladder that never fires looks the same as a broken one.
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
    ///     Returns the domain hint <c>ResponseTruncator</c> closes a hard-truncated response with, keyed by tool.
    /// </summary>
    /// <remarks>
    ///     Lives with the tools so the generic backstop needs no per-tool knowledge, and a new tool contributes
    ///     its hint here. Keyed by tool name alone, it cannot know a report was already written and suggests one
    ///     anyway, which is harmless: the note naming the file is a prefix and survives the cut, so the path is
    ///     still above the footer that repeats the offer.
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