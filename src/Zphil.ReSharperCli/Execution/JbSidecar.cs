using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Defines the naming of every file this server itself writes into a cache home, and the mechanics those
///     files share.
/// </summary>
/// <remarks>
///     One prefix, one key per cache generation, one extension per artifact, so they always sit side by side,
///     and composing, parsing and reading them live in one class a scheme change cannot move half of.
/// </remarks>
internal static class JbSidecar
{
    /// <summary>
    ///     What every sidecar is called before its key: enough to be obviously not <c>jb</c>'s, and enough
    ///     for the sidecars to be found again by something that knows only the cache home.
    /// </summary>
    internal const string Prefix = ".resharper-cli-mcp-";

    /// <summary>
    ///     Computes the key that identifies one cache generation: a short hash of the normalised (solution,
    ///     cache home) pair.
    /// </summary>
    /// <remarks>
    ///     Both paths are absolute by contract (<c>ResolvedConfig</c> resolves them), so normalising here only
    ///     folds separators and Windows casing, and never consults the process working directory. The key is
    ///     one-way: nothing can invert it back into a solution path, and nothing needs to.
    /// </remarks>
    internal static string ComputeKey(string solutionPath, string cacheHome)
    {
        var material = $"{Normalize(solutionPath)}\n{Normalize(cacheHome)}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>
    ///     Returns the path of the sidecar carrying <paramref name="extension" /> for one key, inside the cache
    ///     home itself.
    /// </summary>
    /// <remarks>
    ///     Not in the temp directory, because the cache home is the shared resource: two sessions sharing a
    ///     cache address the same sidecars even when their temp directories differ.
    /// </remarks>
    internal static string PathForKey(string cacheHome, string key, string extension)
    {
        return Path.Combine(Path.GetFullPath(cacheHome), $"{Prefix}{key}.{extension}");
    }

    /// <summary>Returns the same path keyed from the solution, for a caller holding a path rather than a key.</summary>
    internal static string PathFor(string solutionPath, string cacheHome, string extension)
    {
        return PathForKey(cacheHome, ComputeKey(solutionPath, cacheHome), extension);
    }

    /// <summary>
    ///     Finds every sidecar carrying <paramref name="extension" /> under the cache home, with the key its
    ///     file name embeds: the inverse of <see cref="PathForKey" />, for a caller that knows only the cache
    ///     home.
    /// </summary>
    /// <remarks>A cache home that does not exist yet holds nothing.</remarks>
    internal static IEnumerable<(string Key, string SidecarPath)> FindAll(string cacheHome, string extension)
    {
        string home = Path.GetFullPath(cacheHome);
        if (!Directory.Exists(home)) yield break;

        foreach (string sidecarPath in Directory.EnumerateFiles(home, $"{Prefix}*.{extension}"))
            yield return (Path.GetFileNameWithoutExtension(sidecarPath)[Prefix.Length..], sidecarPath);
    }

    /// <summary>
    ///     Opens the sidecar for (re)creation, truncating whatever was there.
    /// </summary>
    /// <remarks>
    ///     <see cref="FileShare.ReadWrite" />: a concurrent server writing or reading the same generation's
    ///     records must not see a sharing violation over what is only a hint or a promise. The lock file must
    ///     conflict, so it does not go through here.
    /// </remarks>
    internal static FileStream OpenToWrite(string solutionPath, string cacheHome, string extension)
    {
        return new FileStream(PathFor(solutionPath, cacheHome, extension), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
    }

    /// <summary>
    ///     Reads the lines of the sidecar at <paramref name="sidecarPath" />, in order, or none when nothing
    ///     has been recorded yet.
    /// </summary>
    /// <remarks>
    ///     Opens with <see cref="FileShare.ReadWrite" />, as <see cref="OpenToWrite" /> does and for the same
    ///     reason. An absent file and an absent cache home both answer "nothing recorded" rather than throwing:
    ///     the first run of a solution meets one, and a caller that has never spawned <c>jb</c> meets the
    ///     other, so left to the callers' catches they would raise, swallow and log an exception on every cold
    ///     run. Any other failure still reaches those catches, which exist for a cache home that is unusable.
    ///     Lines come back byte for byte; whether they may be trimmed is each artifact's own judgement.
    /// </remarks>
    internal static List<string> ReadLines(string sidecarPath)
    {
        try
        {
            using FileStream sidecar = new(sidecarPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(sidecar, Encoding.UTF8);

            List<string> lines = [];
            while (reader.ReadLine() is { } line) lines.Add(line);

            return lines;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    /// <summary>
    ///     Deletes one sidecar. An absent file is no failure, and an ordinary filesystem failure is logged at
    ///     <c>Debug</c> rather than thrown.
    /// </summary>
    /// <remarks>
    ///     No sidecar may fail the call it runs inside; which way each artifact's failures point is its own.
    ///     <paramref name="artifact" /> is how the log names what would not go. The logger is the caller's,
    ///     because a static <c>Serilog.Log</c> here would bypass the <c>ILogger</c> pipeline, render with no
    ///     <c>SourceContext</c>, and be invisible to the test suite.
    /// </remarks>
    internal static void TryDelete(
        string solutionPath,
        string cacheHome,
        string extension,
        string artifact,
        ILogger logger)
    {
        try
        {
            File.Delete(PathFor(solutionPath, cacheHome, extension));
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            logger.LogDebug(exception, "Could not clear the {Artifact} for solution {SolutionPath} in cache home {CacheHome}", artifact, solutionPath, cacheHome);
        }
    }

    /// <summary>
    ///     Folds a path to the spelling the key is computed from: separators trimmed, and Windows casing
    ///     lowered so two spellings of one solution share a lock.
    /// </summary>
    /// <remarks>
    ///     The case fold keys off the operating system rather than the filesystem: the judgement
    ///     <see cref="JbCacheGenerations.NameComparison" /> makes and argues, including what a misread costs
    ///     the lock key folded here.
    /// </remarks>
    private static string Normalize(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }
}