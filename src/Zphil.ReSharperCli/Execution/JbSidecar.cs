using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     The naming scheme of every file this server itself writes into a cache home — the run lock file, the
///     warm marker, the cold tombstone — and the mechanics they share. One prefix, one key per cache
///     generation, one extension per artifact, so the three always sit side by side, and compose
///     (<see cref="PathForKey" />) and parse (<see cref="FindAll" />) live in one class a scheme change
///     cannot move half of.
/// </summary>
internal static class JbSidecar
{
    /// <summary>
    ///     What every sidecar is called before its key: enough to be obviously not <c>jb</c>'s, and enough
    ///     for the sidecars to be found again by something that knows only the cache home.
    /// </summary>
    internal const string Prefix = ".resharper-cli-mcp-";

    /// <summary>
    ///     Identifies one cache generation: a short hash of the normalised (solution, cache home) pair.
    ///     Both paths are absolute by contract — <c>ResolvedConfig</c> resolves them — so normalising here
    ///     only folds separators and Windows casing, and never consults the process working directory. The
    ///     key is one-way: nothing can invert it back into a solution path, and nothing needs to.
    /// </summary>
    internal static string ComputeKey(string solutionPath, string cacheHome)
    {
        var material = $"{Normalize(solutionPath)}\n{Normalize(cacheHome)}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>
    ///     The path of the sidecar carrying <paramref name="extension" /> for one key: inside the cache home
    ///     itself, and deliberately not in the temp directory — the cache home <em>is</em> the shared
    ///     resource, so two sessions sharing a cache address the same sidecars even when their temp
    ///     directories differ.
    /// </summary>
    internal static string PathForKey(string cacheHome, string key, string extension)
    {
        return Path.Combine(Path.GetFullPath(cacheHome), $"{Prefix}{key}.{extension}");
    }

    /// <summary>The same path keyed from the solution, for the callers that hold a path rather than a key.</summary>
    internal static string PathFor(string solutionPath, string cacheHome, string extension)
    {
        return PathForKey(cacheHome, ComputeKey(solutionPath, cacheHome), extension);
    }

    /// <summary>
    ///     Every sidecar carrying <paramref name="extension" /> under the cache home, with the key its file
    ///     name embeds — the inverse of <see cref="PathForKey" />, for a caller that knows only the cache
    ///     home. A cache home that does not exist yet holds nothing.
    /// </summary>
    internal static IEnumerable<(string Key, string SidecarPath)> FindAll(string cacheHome, string extension)
    {
        string home = Path.GetFullPath(cacheHome);
        if (!Directory.Exists(home)) yield break;

        foreach (string sidecarPath in Directory.EnumerateFiles(home, $"{Prefix}*.{extension}"))
            yield return (Path.GetFileNameWithoutExtension(sidecarPath)[Prefix.Length..], sidecarPath);
    }

    /// <summary>
    ///     Open the sidecar for (re)creation, truncating whatever was there.
    /// </summary>
    /// <remarks>
    ///     <see cref="FileShare.ReadWrite" />: a concurrent server writing or reading the same generation's
    ///     records must not see a sharing violation over what is only a hint or a promise. The lock file —
    ///     which must conflict, that being the point of it — does not go through here.
    /// </remarks>
    internal static FileStream OpenToWrite(string solutionPath, string cacheHome, string extension)
    {
        return new FileStream(PathFor(solutionPath, cacheHome, extension), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
    }

    /// <summary>
    ///     Delete one sidecar, treating both an absent file and an ordinary filesystem failure as quiet
    ///     outcomes — each artifact's caller documents which way its failures are allowed to point, and none
    ///     of them may fail the call it runs inside. <paramref name="artifact" /> is how the log names what
    ///     would not go.
    /// </summary>
    internal static void TryDelete(string solutionPath, string cacheHome, string extension, string artifact)
    {
        try
        {
            File.Delete(PathFor(solutionPath, cacheHome, extension));
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            Log.Debug(exception, "Could not clear the {Artifact} for solution {SolutionPath} in cache home {CacheHome}", artifact, solutionPath, cacheHome);
        }
    }

    private static string Normalize(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }
}