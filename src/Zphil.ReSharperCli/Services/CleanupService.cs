using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Discovery;

namespace Zphil.ReSharperCli.Services;

/// <summary>
///     Runs <c>jb cleanupcode</c> in place over the given files with a named profile, returning a structured
///     <see cref="CleanupOutcome" />: the profile plus a per-entry <see cref="CleanupFileStatus" />
///     classification computed by hashing each concrete file before and after the run, so the caller can see
///     which files cleanup actually rewrote. Formatting lives in <c>CleanupSummaryFormatter</c>. Mutating: a
///     non-zero exit (e.g. an unknown profile, or an <c>--include</c> set that matched nothing) surfaces from
///     <see cref="JbRunner" /> and is restated here as a failed pass — see <see cref="FailedPassMessage" /> —
///     rather than being silently swallowed.
/// </summary>
internal sealed class CleanupService(JbRunner jbRunner, ILogger<CleanupService> logger)
{
    /// <summary>The profile applied when the caller does not specify one.</summary>
    public const string DefaultProfile = "Built-in: Full Cleanup";

    /// <summary>
    ///     Clean up <paramref name="files" /> in <paramref name="config" />'s solution with
    ///     <paramref name="profile" />, and classify what changed.
    /// </summary>
    /// <remarks>
    ///     <paramref name="onProgress" /> is passed straight through, as inspect's is, and is omitted when
    ///     the caller has nowhere to report to.
    /// </remarks>
    public async Task<CleanupOutcome> RunAsync(
        ResolvedConfig config,
        IReadOnlyList<string> files,
        string? profile,
        CancellationToken cancellationToken,
        Action<string>? onProgress = null)
    {
        // An unspecified profile resolves to the solution's own declared profile before the built-in
        // default, so a repo that narrowed its cleanup gets that narrowing on every call — including the
        // calls of an agent that does not know the profile exists. A blank argument reads as unspecified,
        // matching how a blank declared profile reads. Resolved here rather than by the caller so every
        // entry into cleanup gets the same chain, and the profile this run reports is the one it used.
        string resolvedProfile = CleanupProfileReader.Normalize(profile)
                                 ?? config.CleanupProfile
                                 ?? DefaultProfile;

        // This tool mutates files in place, so verify concrete paths exist before invoking jb — a typo
        // should fail fast and name the offending path, not silently clean up nothing.
        string solutionDirectory = config.SolutionDirectory;
        List<string> missing = FilePathList.FindMissing(files, solutionDirectory);
        if (missing.Count > 0)
            throw new UserErrorException(
                $"The following files were not found (relative to the solution root \"{solutionDirectory}\", or absolute):\n"
                + string.Join("\n", missing.Select(file => $"  - {file}")));

        // Snapshot each concrete file's content hash before the run. Index-aligned with files (not a dict:
        // duplicate entries and case-insensitive Windows paths would collide, and the displayed entry must
        // stay aligned). Wildcards get no snapshot — jb expands them, so they are never a single file.
        var beforeHashes = new List<byte[]?>(files.Count);
        foreach (string entry in files)
            beforeHashes.Add(
                FilePathList.IsPattern(entry) ? null : HashFile(FilePathList.Resolve(entry, solutionDirectory)));

        List<string> arguments = BuildArguments(config, files, resolvedProfile);

        try
        {
            await jbRunner.RunAsync(config, arguments, cancellationToken, onProgress);
        }
        catch (JbExitCodeException exception)
        {
            throw new UserErrorException(FailedPassMessage(exception, files, solutionDirectory), exception);
        }

        // jb has exited (ProcessRunner awaits WaitForExitAsync), so re-hash and classify. This is pure
        // observability: a hash-read failure must never turn a cleanup jb already performed into an error.
        var entries = new List<CleanupEntry>(files.Count);
        for (var i = 0; i < files.Count; i++)
            entries.Add(new CleanupEntry(files[i], Classify(files[i], beforeHashes[i], solutionDirectory)));

        // Debug: this is already in the response, and its interest to the log is the ratio over time rather
        // than any one call. A profile that stopped matching anything shows up here as pass after pass
        // rewriting nothing — the shape the exit-code-3 "No items were found to cleanup" defect had, which an
        // agent read as "nothing needed changing" and moved on from.
        logger.LogDebug(
            "jb cleanupcode with profile {CleanupProfile} rewrote {ChangedCount} of {RequestedCount} requested entries",
            resolvedProfile,
            entries.Count(entry => entry.Status == CleanupFileStatus.Changed),
            files.Count);

        return new CleanupOutcome(resolvedProfile, entries);
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
            JbRunner.IncludeArgument(files, config.SolutionDirectory)
        ];

        JbRunner.AppendConfigArguments(arguments, config);

        return arguments;
    }

    /// <summary>
    ///     Restate a non-zero <c>jb cleanupcode</c> exit in cleanup's own terms. The failure that made this
    ///     necessary reads as a success: <c>jb</c> exits 3 with "No items were found to cleanup", and an agent
    ///     that has just made 27 edits reads that tail as "nothing needed changing" and moves on — which is how
    ///     a whole cleanup pass got skipped in the field. Only this class knows the caller named specific files
    ///     and got none of them, so only this class can say so, and it lists the patterns <c>jb</c> was
    ///     actually given (translated, unlike the report's own entries) because that spelling is the thing the
    ///     caller cannot see.
    /// </summary>
    private static string FailedPassMessage(
        JbExitCodeException failure,
        IReadOnlyList<string> files,
        string solutionDirectory)
    {
        IEnumerable<string> patterns = files.Select(entry => FilePathList.ToIncludePattern(entry, solutionDirectory));
        IEnumerable<string> lines = patterns.Select(pattern => $"  - {pattern}");
        string listed = string.Join("\n", lines);

        // Unbounded, as the missing-files error above is: a caller that named 27 files is owed all 27.
        string reported = failure.StandardErrorTail.Length > 0
            ? $"jb reported: {failure.StandardErrorTail}\n"
            : string.Empty;

        return $"jb cleanupcode exited with code {failure.ExitCode}. No file was cleaned up — treat this as a "
               + "failed pass, not as \"nothing needed changing\".\n"
               + reported
               + $"The {files.Count} --include pattern(s) it was given:\n"
               + listed
               + "\njb matches --include against the files that belong to a project in the solution. A file "
               + "that is on disk but in no project matches nothing.";
    }

    /// <summary>
    ///     Classify one requested entry against its pre-run hash. A wildcard (see
    ///     <see cref="FilePathList.IsPattern" />) is <see cref="CleanupFileStatus.Pattern" />; an unreadable before- or
    ///     after-state is
    ///     <see cref="CleanupFileStatus.StatusUnknown" />; otherwise the entry is
    ///     <see cref="CleanupFileStatus.Changed" /> or <see cref="CleanupFileStatus.Unchanged" /> by hash
    ///     equality.
    /// </summary>
    private static CleanupFileStatus Classify(string entry, byte[]? beforeHash, string solutionDirectory)
    {
        if (FilePathList.IsPattern(entry)) return CleanupFileStatus.Pattern;

        byte[]? afterHash = HashFile(FilePathList.Resolve(entry, solutionDirectory));
        if (beforeHash is null || afterHash is null) return CleanupFileStatus.StatusUnknown;

        return beforeHash.AsSpan().SequenceEqual(afterHash)
            ? CleanupFileStatus.Unchanged
            : CleanupFileStatus.Changed;
    }

    /// <summary>
    ///     SHA-256 of the file's content, or <see langword="null" /> if it cannot be read. Content hashing is
    ///     deliberate: <c>(length, mtime)</c> false-positives on a touch-with-identical-content and
    ///     false-negatives on a same-length edit, while holding the raw bytes would pin every before-buffer
    ///     across a jb run that can last minutes. Never throws — a transient lock (AV/indexer) or a file jb deleted
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
}