using System.Text;
using Microsoft.Extensions.Logging;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     A file inside the cache home whose modification time records when a <c>jb</c> run against that cache
///     generation last <em>succeeded</em>, and whose content names the generation directory that run left
///     behind. The speculative pre-warm reads the timestamp to skip a generation something has already
///     warmed, a transplant reads the name to find a donor worth copying, and — because every successful run
///     through <see cref="Services.JbRunner" /> stamps it, a foreground tool call included — a transplant
///     reads its mere <see cref="Exists">existence</see> to tell a cache some run produced from the
///     part-built remnant of one that never finished.
/// </summary>
/// <remarks>
///     <para>
///         Deliberately not the <see cref="JbRunLock" /> lock file's own timestamp, even though the two sit
///         side by side under the same key. The lock file's mtime moves when a run <em>starts</em> and again
///         when one <em>fails</em>, while the debounce needs "when did one last succeed"; a lock file held
///         <see cref="FileShare.None" /> cannot be stamped from a second handle anyway; and the lock is
///         load-bearing correctness where this is only a hint, so a bug here must not be able to break it.
///         The two share only <see cref="JbSidecar" />'s naming; <see cref="JbRunLock" /> never references
///         the marker.
///     </para>
///     <para>
///         Every filesystem failure is swallowed, because a marker that cannot be written or read is not a
///         reason to fail — or to log against — a run the user asked for. Every failure mode also reads as
///         the answer that can only cost work: a missing file, an unreadable one, and a future-dated one all
///         report stale, and anything short of a directory this server can name reports no generation, so
///         those two readers can permit a redundant pre-warm or forgo a copy but never suppress the one or
///         misdirect the other. <see cref="Exists" /> fails the other way round for the same reason, since
///         its caller's only use for <see langword="false" /> is to delete: an unanswerable question reads as
///         a cache worth protecting.
///     </para>
///     <para>
///         The single exception to the silence is <see cref="WarnOnceAboutUnrecognisedNaming" />, and it is
///         not a filesystem failure: it says the derivation this server makes from <c>jb</c>'s directory
///         naming has stopped matching what <c>jb</c> writes. Nothing breaks when it fires — the features
///         reading the name switch themselves off — but nothing else would ever say so, which is why it is a
///         warning, and why it is said once.
///     </para>
/// </remarks>
internal static class JbWarmMarker
{
    private const string Extension = "warm";

    /// <summary>
    ///     Whether the "no generation matched this solution's hash" warning has already been logged. The
    ///     condition means <c>jb</c>'s directory naming no longer matches what
    ///     <see cref="JbSolutionCacheHash" /> reproduces, which is one fact about this machine's <c>jb</c>
    ///     rather than one fact per run — logging it on every stamp would bury the session in a repeat of the
    ///     same sentence.
    /// </summary>
    private static int _driftWarned;

    /// <summary>
    ///     Where the marker for one cache generation lives: beside its lock file and cold tombstone, under
    ///     <see cref="JbSidecar" />'s one key for the generation.
    /// </summary>
    internal static string PathFor(string solutionPath, string cacheHome)
    {
        return JbSidecar.PathFor(solutionPath, cacheHome, Extension);
    }

    /// <summary>
    ///     Every warm marker under <paramref name="cacheHome" />, with the key its file name carries. For
    ///     donor discovery, which starts from nothing but the cache home: the key is one-way, so the owning
    ///     solution's path is not recoverable — and does not need to be, because the generation's name is
    ///     read from the marker's content and the donor's lock is taken by key.
    /// </summary>
    internal static IEnumerable<(string Key, string MarkerPath)> FindAll(string cacheHome)
    {
        return JbSidecar.FindAll(cacheHome, Extension);
    }

    /// <summary>
    ///     Record that a <c>jb</c> run against this cache generation has just succeeded, and which generation
    ///     directory it left warm. The modification time is the debounce's whole payload; the content is what
    ///     lets a <em>different</em> solution find this one, since the marker's own file name is a key
    ///     nothing can invert back into a solution path.
    /// </summary>
    /// <remarks>
    ///     Named from the outside, by matching this solution's computed hash against the directories actually
    ///     on disk, rather than composed from the hash alone: what is wanted is the generation <c>jb</c> just
    ///     used, forks included, and only the filesystem knows which of those exists. Finding none leaves the
    ///     file empty — exactly the marker this used to write — so every reader that wants a name is told
    ///     there is none, and the features built on it switch themselves off rather than acting on a guess.
    /// </remarks>
    internal static void Stamp(string solutionPath, string cacheHome, ILogger logger)
    {
        try
        {
            string? generationName = WarmedGenerationName(solutionPath, cacheHome);

            using FileStream marker = JbSidecar.OpenToWrite(solutionPath, cacheHome, Extension);

            if (generationName is null)
            {
                WarnOnceAboutUnrecognisedNaming(solutionPath, cacheHome, logger);
                return;
            }

            marker.Write(Encoding.UTF8.GetBytes(generationName));

            // The mechanism donor discovery depends on, and the one step of it nothing else records: a
            // generation no marker names can never be copied, however warm it is.
            logger.LogDebug(
                "Stamped the jb warm marker for solution {SolutionPath}, naming cache generation {GenerationName}",
                solutionPath,
                generationName);
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            logger.LogDebug(exception, "Could not stamp the jb warm marker for solution {SolutionPath} in cache home {CacheHome}", solutionPath, cacheHome);
        }
    }

    /// <summary>
    ///     The cache generation directory named by the marker file at <paramref name="markerFilePath" />, or
    ///     <see langword="null" /> when it names none this server should act on. Takes the marker's path
    ///     rather than a solution path because the caller that needs this — donor discovery — is reading
    ///     <em>another</em> solution's marker, and has nothing but the file to go on.
    /// </summary>
    /// <remarks>
    ///     Every uncertainty answers <see langword="null" />: a marker written before this content existed, an
    ///     empty one written under naming drift, one whose generation has since been deleted, and one whose
    ///     content is not a bare directory name at all. The last is a guard rather than a formality — the
    ///     content is combined with a cache home to make a path a caller then copies from, so anything
    ///     carrying a separator, a drive, or a parent reference is refused before it can address a directory
    ///     outside the cache home.
    /// </remarks>
    internal static string? TryReadGenerationName(string markerFilePath, string cacheHome, ILogger logger)
    {
        try
        {
            using FileStream file = new(markerFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(file, Encoding.UTF8);
            string content = reader.ReadToEnd().Trim();

            if (!IsBareDirectoryName(content)) return null;

            return Directory.Exists(JbCacheGenerations.PathUnder(cacheHome, content)) ? content : null;
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            logger.LogDebug(exception, "Could not read the jb warm marker {MarkerFilePath}", markerFilePath);
            return null;
        }
    }

    /// <summary>
    ///     Forget that a run against this cache generation ever succeeded — for a caller that has just dropped
    ///     the generation the marker describes, leaving the claim behind untrue. Swallows failures like
    ///     <see cref="Stamp" /> does, and deleting a marker that was never there is not one: the cost of a
    ///     marker that will not go is a redundant pre-warm, which is the direction this file is always
    ///     allowed to fail in.
    /// </summary>
    internal static void Clear(string solutionPath, string cacheHome, ILogger logger)
    {
        JbSidecar.TryDelete(solutionPath, cacheHome, Extension, "jb warm marker", logger);
    }

    /// <summary>
    ///     How long ago a run against this cache generation last succeeded, or <see langword="null" /> when
    ///     none is recorded — no marker, or one this server cannot read. The one mtime read behind both
    ///     <see cref="IsFreshWithin" />'s debounce and the cache-state line a run starts with, so "recently
    ///     warm" and "warm, and this old" cannot come to disagree.
    /// </summary>
    /// <remarks>
    ///     A negative span is possible and is passed through rather than hidden: a moved clock or a cache home
    ///     copied between machines dates a marker into the future, and each caller decides what to do with
    ///     that — the debounce reads it as stale so it cannot suppress pre-warming forever, while the log
    ///     shows the operator the nonsense the filesystem is reporting.
    /// </remarks>
    internal static TimeSpan? Age(string solutionPath, string cacheHome, ILogger logger)
    {
        try
        {
            FileInfo marker = new(PathFor(solutionPath, cacheHome));
            return marker.Exists ? DateTime.UtcNow - marker.LastWriteTimeUtc : null;
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            logger.LogDebug(exception, "Could not read the jb warm marker for solution {SolutionPath} in cache home {CacheHome}", solutionPath, cacheHome);
            return null;
        }
    }

    /// <summary>
    ///     Whether a run against this cache generation succeeded within <paramref name="window" />. A missing
    ///     or unreadable marker reads as stale, and so does a future-dated one — a moved clock, or a cache
    ///     home copied between machines — so it cannot suppress pre-warming forever.
    /// </summary>
    internal static bool IsFreshWithin(string solutionPath, string cacheHome, TimeSpan window, ILogger logger)
    {
        return Age(solutionPath, cacheHome, logger) is { } age && age >= TimeSpan.Zero && age < window;
    }

    /// <summary>
    ///     Whether a <c>jb</c> run against this cache generation has ever succeeded, whenever that was. Asked
    ///     by a transplant looking at directories that are already there: a marker means some run produced
    ///     them and they are the solution's own, while no marker at all means no run ever finished and what is
    ///     on disk is the part-built remnant of one that was killed.
    /// </summary>
    /// <remarks>
    ///     Content is not read, so every marker protects — including the empty one an older build of this
    ///     server wrote and the empty one naming drift still writes today. Existence is the whole statement,
    ///     which is what keeps the question answerable by markers written before it was ever asked.
    ///     <para>
    ///         Anything that goes wrong answers <see langword="true" />, the mirror of
    ///         <see cref="JbColdTombstone.Exists" /> failing towards "reset": the caller's only use for a
    ///         <see langword="false" /> is to delete a directory, and it must not do that on a question this
    ///         could not answer. <see cref="File.Exists(string)" /> does not throw, so what the catch covers
    ///         is a key that cannot be <em>derived</em> — an unusable cache home. A marker present but
    ///         unreadable needs no handling here for a reason worth stating: reading it is the caller's next
    ///         step only after finding a donor, and a cache home too broken to read this file hides every
    ///         donor marker from that search too, so no replace can reach the delete.
    ///     </para>
    /// </remarks>
    internal static bool Exists(string solutionPath, string cacheHome, ILogger logger)
    {
        try
        {
            return File.Exists(PathFor(solutionPath, cacheHome));
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            logger.LogDebug(exception, "Could not look for the jb warm marker for solution {SolutionPath} in cache home {CacheHome}", solutionPath, cacheHome);
            return true;
        }
    }

    /// <summary>
    ///     Which generation directory under <paramref name="cacheHome" /> the run that just succeeded left
    ///     warm: the one whose name carries this solution's computed hash, and — where <c>jb</c> has forked
    ///     the generation — the most recently written of them, since that is the one it can only just have
    ///     closed. Ties break towards the higher generation number, which is the later fork.
    /// </summary>
    private static string? WarmedGenerationName(string solutionPath, string cacheHome)
    {
        return JbCacheGenerations.FindFor(cacheHome, solutionPath).Owned
            .OrderByDescending(generation => Directory.GetLastWriteTimeUtc(generation.FullPath))
            .ThenByDescending(generation => generation.Name, StringComparer.Ordinal)
            .Select(generation => generation.Name)
            .FirstOrDefault();
    }

    /// <summary>
    ///     A run succeeded and left no directory this server can recognise as its cache generation, so
    ///     <c>jb</c>'s naming has moved away from what <see cref="JbSolutionCacheHash" /> reproduces. Nothing
    ///     is broken by it — every feature reading the name simply stops finding one — but it is the single
    ///     signal that the derivation has gone stale, so it is a warning rather than a debug line, and said
    ///     once.
    /// </summary>
    private static void WarnOnceAboutUnrecognisedNaming(string solutionPath, string cacheHome, ILogger logger)
    {
        if (Interlocked.Exchange(ref _driftWarned, 1) != 0) return;

        logger.LogWarning(
            "A jb run against solution {SolutionPath} succeeded but left no cache generation directory matching its computed hash under {CacheHome}; "
            + "features that need to name a cache generation are disabled for this server",
            solutionPath,
            cacheHome);
    }

    /// <summary>
    ///     Whether <paramref name="content" /> is a directory name and nothing more — no separator, no drive,
    ///     no <c>.</c> or <c>..</c>. The equality against <see cref="Path.GetFileName(string)" /> rejects
    ///     every form that carries a path; the dot cases are spelled out because they survive it.
    /// </summary>
    private static bool IsBareDirectoryName(string content)
    {
        if (content.Length == 0 || content is "." or "..") return false;

        return string.Equals(content, Path.GetFileName(content), StringComparison.Ordinal);
    }
}