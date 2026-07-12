using System.Security.Cryptography;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Services;

/// <summary>
///     Runs <c>jb cleanupcode</c> in place over the given files with a named profile, returning a structured
///     <see cref="CleanupOutcome" />: the profile plus a per-entry <see cref="CleanupFileStatus" />
///     classification computed by hashing each concrete file before and after the run, so the caller can see
///     which files cleanup actually rewrote. Formatting lives in <c>CleanupSummaryFormatter</c>. Mutating: a
///     non-zero exit (e.g. an unknown profile) is surfaced as a <see cref="UserErrorException" /> rather than
///     silently swallowed.
/// </summary>
internal sealed class CleanupService(IProcessRunner processRunner)
{
    /// <summary>The profile applied when the caller does not specify one.</summary>
    public const string DefaultProfile = "Built-in: Full Cleanup";

    private const int StandardErrorTailLength = 2000;

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public async Task<CleanupOutcome> RunAsync(
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

        // Snapshot each concrete file's content hash before the run. Index-aligned with files (not a dict:
        // duplicate entries and case-insensitive Windows paths would collide, and the displayed entry must
        // stay aligned). Wildcards get no snapshot — jb expands them, so they are never a single file.
        var beforeHashes = new List<byte[]?>(files.Count);
        foreach (string entry in files)
            beforeHashes.Add(IsPattern(entry) ? null : HashFile(Path.GetFullPath(entry, solutionDirectory)));

        var arguments = BuildArguments(config, files, profile);

        ProcessResult result = await processRunner.RunAsync(
            config.JbExecutablePath, arguments, Timeout, cancellationToken);

        if (result.ExitCode != 0)
            throw new UserErrorException(
                $"jb cleanupcode exited with code {result.ExitCode}.\n{StandardErrorTail(result.StandardError)}");

        // jb has exited (ProcessRunner awaits WaitForExitAsync), so re-hash and classify. This is pure
        // observability: a hash-read failure must never turn a cleanup jb already performed into an error.
        var entries = new List<CleanupEntry>(files.Count);
        for (var i = 0; i < files.Count; i++)
            entries.Add(new CleanupEntry(files[i], Classify(files[i], beforeHashes[i], solutionDirectory)));

        return new CleanupOutcome(profile, entries);
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
    ///     patterns (see <see cref="IsPattern" />) are left for jb to expand and are never reported; other
    ///     entries are resolved against <paramref name="solutionDirectory" /> (absolute entries ignore it).
    /// </summary>
    internal static List<string> FindMissingFiles(IReadOnlyList<string> files, string solutionDirectory)
    {
        List<string> missing = [];
        foreach (string entry in files)
        {
            if (IsPattern(entry)) continue;

            string resolved = Path.GetFullPath(entry, solutionDirectory);
            if (!File.Exists(resolved)) missing.Add(entry);
        }

        return missing;
    }

    /// <summary>
    ///     A <c>files</c> entry is a wildcard pattern (handed to jb unexpanded, never a single file) when it
    ///     contains <c>*</c>, <c>?</c>, or <c>[</c>. Shared by missing-file validation and hash classification
    ///     so the rule cannot drift between them.
    /// </summary>
    private static bool IsPattern(string entry)
    {
        return entry.AsSpan().IndexOfAny('*', '?', '[') >= 0;
    }

    /// <summary>
    ///     Classify one requested entry against its pre-run hash. A wildcard is
    ///     <see cref="CleanupFileStatus.Pattern" />; an unreadable before- or after-state is
    ///     <see cref="CleanupFileStatus.StatusUnknown" />; otherwise the entry is
    ///     <see cref="CleanupFileStatus.Changed" /> or <see cref="CleanupFileStatus.Unchanged" /> by hash
    ///     equality.
    /// </summary>
    private static CleanupFileStatus Classify(string entry, byte[]? beforeHash, string solutionDirectory)
    {
        if (IsPattern(entry)) return CleanupFileStatus.Pattern;

        byte[]? afterHash = HashFile(Path.GetFullPath(entry, solutionDirectory));
        if (beforeHash is null || afterHash is null) return CleanupFileStatus.StatusUnknown;

        return beforeHash.AsSpan().SequenceEqual(afterHash)
            ? CleanupFileStatus.Unchanged
            : CleanupFileStatus.Changed;
    }

    /// <summary>
    ///     SHA-256 of the file's content, or <see langword="null" /> if it cannot be read. Content hashing is
    ///     deliberate: <c>(length, mtime)</c> false-positives on a touch-with-identical-content and
    ///     false-negatives on a same-length edit, while holding the raw bytes would pin every before-buffer
    ///     across the up-to-5-minute jb run. Never throws — a transient lock (AV/indexer) or a file jb deleted
    ///     must not turn a completed cleanup into a reported error.
    /// </summary>
    private static byte[]? HashFile(string resolvedPath)
    {
        try
        {
            using FileStream stream = File.OpenRead(resolvedPath);
            return SHA256.HashData(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string StandardErrorTail(string standardError)
    {
        string trimmed = standardError.TrimEnd();
        return trimmed.Length <= StandardErrorTailLength ? trimmed : trimmed[^StandardErrorTailLength..];
    }
}