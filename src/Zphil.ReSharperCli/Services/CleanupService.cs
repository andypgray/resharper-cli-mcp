using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Services;

/// <summary>
///     Runs <c>jb cleanupcode</c> in place over the given files with a named profile, returning a plain
///     summary. Mutating: it rewrites the user's files, so a non-zero exit (e.g. an unknown profile) is
///     surfaced as a <see cref="UserErrorException" /> rather than silently swallowed.
/// </summary>
internal sealed class CleanupService(IProcessRunner processRunner)
{
    /// <summary>The profile applied when the caller does not specify one.</summary>
    public const string DefaultProfile = "Built-in: Full Cleanup";

    private const int StandardErrorTailLength = 2000;

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public async Task<string> RunAsync(
        ResolvedConfig config,
        IReadOnlyList<string> files,
        string profile,
        CancellationToken cancellationToken)
    {
        // This tool mutates files in place, so verify concrete paths exist before invoking jb — a typo
        // should fail fast and name the offending path, not silently clean up nothing.
        string solutionDirectory = Path.GetDirectoryName(config.SolutionPath)!;
        var missing = FindMissingFiles(files, solutionDirectory);
        if (missing.Count > 0)
            throw new UserErrorException(
                $"The following files were not found (relative to the solution root \"{solutionDirectory}\", or absolute):\n"
                + string.Join("\n", missing.Select(file => $"  - {file}")));

        var arguments = BuildArguments(config, files, profile);

        ProcessResult result = await processRunner.RunAsync(
            config.JbExecutablePath, arguments, Timeout, cancellationToken);

        if (result.ExitCode != 0)
            throw new UserErrorException(
                $"jb cleanupcode exited with code {result.ExitCode}.\n{StandardErrorTail(result.StandardError)}");

        return BuildSummary(files, profile);
    }

    /// <summary>Build the <c>jb cleanupcode</c> argument list. Order is pinned by tests.</summary>
    internal static List<string> BuildArguments(
        ResolvedConfig config,
        IReadOnlyList<string> files,
        string profile)
    {
        List<string> arguments =
        [
            "cleanupcode",
            config.SolutionPath,
            $"--profile={profile}",
            "--no-build",
            $"--include={string.Join(";", files)}",
            $"--caches-home={config.CacheHome}"
        ];

        if (config.SettingsPath is not null) arguments.Add($"--settings={config.SettingsPath}");

        if (!string.IsNullOrEmpty(config.Extensions)) arguments.Add($"-x={config.Extensions}");

        if (!string.IsNullOrEmpty(config.ExtensionSource)) arguments.Add($"--source={config.ExtensionSource}");

        return arguments;
    }

    /// <summary>
    ///     Return the entries in <paramref name="files" /> that do not resolve to an existing file. Wildcard
    ///     patterns (containing <c>*</c>, <c>?</c>, or <c>[</c>) are left for jb to expand and are never
    ///     reported; other entries are resolved against <paramref name="solutionDirectory" /> (absolute
    ///     entries ignore it).
    /// </summary>
    internal static List<string> FindMissingFiles(IReadOnlyList<string> files, string solutionDirectory)
    {
        List<string> missing = [];
        foreach (string entry in files)
        {
            if (entry.AsSpan().IndexOfAny('*', '?', '[') >= 0) continue;

            string resolved = Path.GetFullPath(entry, solutionDirectory);
            if (!File.Exists(resolved)) missing.Add(entry);
        }

        return missing;
    }

    private static string BuildSummary(IReadOnlyList<string> files, string profile)
    {
        List<string> lines = [$"Cleanup completed for {files.Count} file(s) with profile \"{profile}\":"];
        foreach (string file in files) lines.Add($"  - {file}");

        return string.Join("\n", lines);
    }

    private static string StandardErrorTail(string standardError)
    {
        string trimmed = standardError.TrimEnd();
        return trimmed.Length <= StandardErrorTailLength ? trimmed : trimmed[^StandardErrorTailLength..];
    }
}