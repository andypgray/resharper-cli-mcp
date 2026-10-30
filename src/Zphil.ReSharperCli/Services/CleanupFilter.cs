using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Services;

/// <summary>
///     Represents the solution filter one cleanup runs through: where it is written, what it holds, and how much
///     of the solution it loads.
/// </summary>
internal sealed record SolutionFilterPlan(string Path, string Content, int ProjectCount, int SolutionProjectCount);

/// <summary>
///     Represents which projects a cleanup loads: a filter, or the whole solution and the reason no filter was
///     used.
/// </summary>
/// <remarks>Exactly one of the two is set.</remarks>
internal sealed record CleanupScope(SolutionFilterPlan? Filter, string? SolutionWideBecause)
{
    public static CleanupScope Through(SolutionFilterPlan filter)
    {
        return new CleanupScope(filter, null);
    }

    public static CleanupScope SolutionWide(string reason)
    {
        return new CleanupScope(null, reason);
    }
}

/// <summary>
///     Decides which projects a cleanup has <c>jb</c> load, and owns the one solution filter per solution that
///     narrows it.
/// </summary>
/// <remarks>
///     <para>
///         Most of a cleanup's time is <c>jb</c> loading the solution, a cost that tracks the projects loaded
///         rather than the files rewritten. A filter holding the projects that own the files, and every project
///         they reference transitively, loads less and rewrites the same bytes, because every type those files
///         can see is still loaded from source.
///     </para>
///     <para>
///         The filter is one file per run-lock lease, at <see cref="PathFor" />, written by <see cref="JbRunner" />
///         while that lease is held. <c>jb</c> opens the cache generation of the solution a filter names, so a
///         filtered run shares the solution's cache rather than building one of its own.
///     </para>
///     <para>
///         Its <paramref name="rootDirectory" /> is injected, as <see cref="InspectReportWriter" />'s is, so a
///         test can point it at a directory of its own. <paramref name="enabled" /> is the
///         <see cref="EnableVariable" /> switch, read once at the composition root.
///     </para>
/// </remarks>
internal sealed class CleanupFilter(string rootDirectory, bool enabled, ILogger<CleanupFilter> logger)
{
    /// <summary>Environment variable that turns the filter off. Documented spelling: <c>off</c>.</summary>
    internal const string EnableVariable = "RESHARPER_MCP_CLEANUP_FILTER";

    /// <summary>
    ///     Returns where the filter for <paramref name="solutionPath" /> is written, whether or not it has been.
    /// </summary>
    /// <remarks>
    ///     Named <c>&lt;solution&gt;.cleanup-&lt;key&gt;.slnf</c>, the key the run lock's own, so one filter
    ///     belongs to exactly one lease: the filter is written under that lease, and two runs holding different
    ///     ones must never share a file.
    /// </remarks>
    public string PathFor(string solutionPath, string cacheHome)
    {
        string name = Path.GetFileNameWithoutExtension(solutionPath);
        string key = JbSidecar.ComputeKey(solutionPath, cacheHome);

        return Path.Combine(rootDirectory, "Zphil.ReSharperCli", "cleanup-filters", $"{name}.cleanup-{key}.slnf");
    }

    /// <summary>
    ///     Decides whether a cleanup of <paramref name="files" /> runs through a filter, and logs the decision.
    /// </summary>
    /// <remarks>
    ///     <paramref name="files" /> has already been validated, so every entry that is not a wildcard names a file
    ///     that exists. Every reason to run solution-wide is one where a filter could leave out a project the files
    ///     need, or would load every project anyway.
    /// </remarks>
    public CleanupScope Decide(ResolvedConfig config, IReadOnlyList<string> files)
    {
        CleanupScope scope = Plan(config, files);

        if (scope.Filter is { } filter)
            logger.LogInformation(
                "Cleanup of {SolutionPath} loads {ProjectCount} of {SolutionProjectCount} projects through {FilterPath}",
                config.SolutionPath,
                filter.ProjectCount,
                filter.SolutionProjectCount,
                filter.Path);
        else
            logger.LogInformation(
                "Cleanup of {SolutionPath} loads the whole solution: {Reason}",
                config.SolutionPath,
                scope.SolutionWideBecause);

        return scope;
    }

    private CleanupScope Plan(ResolvedConfig config, IReadOnlyList<string> files)
    {
        if (!enabled) return CleanupScope.SolutionWide($"{EnableVariable} is off");

        if (SolutionFilterFile.IsFilter(config.SolutionPath))
            return CleanupScope.SolutionWide("the solution is already a solution filter");

        if (files.FirstOrDefault(FilePathList.IsPattern) is { } pattern)
            return CleanupScope.SolutionWide($"\"{pattern}\" is a wildcard, which only jb can expand");

        ModelReading<IReadOnlyList<SolutionProject>> solution = SolutionModel.ReadProjects(config.SolutionPath);
        if (solution.Value is null) return CleanupScope.SolutionWide(solution.Declined!);

        ILookup<string, SolutionProject> byDirectory = solution.Value.ToLookup(
            project => Path.GetDirectoryName(project.FullPath)!, SolutionModel.PathComparer);

        HashSet<SolutionProject> owners = [];
        foreach (string entry in files)
        {
            ModelReading<SolutionProject> owner = OwnerOf(FilePathList.Resolve(entry, config.SolutionDirectory), byDirectory);
            if (owner.Value is null) return CleanupScope.SolutionWide($"\"{entry}\": {owner.Declined}");

            owners.Add(owner.Value);
        }

        ModelReading<IReadOnlyList<SolutionProject>> closure = ProjectGraph.Closure(solution.Value, owners);
        if (closure.Value is null) return CleanupScope.SolutionWide(closure.Declined!);

        int total = solution.Value.Count;
        if (closure.Value.Count == total)
            return CleanupScope.SolutionWide($"the files' projects and their references are all {total} of the solution's projects");

        string content = SolutionFilterFile.Compose(config.SolutionPath, closure.Value);
        return CleanupScope.Through(new SolutionFilterPlan(PathFor(config.SolutionPath, config.CacheHome), content, closure.Value.Count, total));
    }

    /// <summary>
    ///     Returns the project that owns <paramref name="file" />: the one project in the nearest directory above
    ///     it that holds any.
    /// </summary>
    /// <remarks>
    ///     The nearest such directory is the one an SDK project's default globs would draw the file from. Two
    ///     projects in that directory leave the owner a guess, so it declines instead.
    /// </remarks>
    private static ModelReading<SolutionProject> OwnerOf(string file, ILookup<string, SolutionProject> byDirectory)
    {
        for (DirectoryInfo? directory = new FileInfo(file).Directory; directory is not null; directory = directory.Parent)
        {
            List<SolutionProject> candidates = byDirectory[directory.FullName].ToList();
            if (candidates.Count == 1) return ModelReading<SolutionProject>.Of(candidates[0]);

            if (candidates.Count > 1)
                return ModelReading<SolutionProject>.Decline($"{directory.FullName} holds {candidates.Count} of the solution's projects, so which one owns it is ambiguous");
        }

        return ModelReading<SolutionProject>.Decline("no project of the solution is in a directory above it");
    }
}