using System.Text;
using Microsoft.Extensions.Logging;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     What <see cref="JbWarmMarker.Stamp" /> managed to record — the one thing about a stamp its caller
///     cannot read back off the marker afterwards.
/// </summary>
/// <remarks>
///     Three outcomes rather than a bool, because the last two leave the same evidence and mean opposite
///     things. <see cref="NoGenerationMatched" /> is a marker written under <c>jb</c> naming this server no
///     longer recognises — a fact about <c>jb</c> worth saying out loud — while <see cref="NotStamped" /> is
///     an ordinary filesystem failure that wrote nothing at all. Folded into one answer, an unwritable cache
///     home would accuse <c>jb</c> of drift.
/// </remarks>
internal enum StampOutcome
{
    /// <summary>The marker was written and names the cache generation the run left warm.</summary>
    NamedGeneration,

    /// <summary>
    ///     The marker was written and names nothing, because no directory under the cache home carries this
    ///     solution's computed hash: <c>jb</c>'s naming has moved away from what
    ///     <see cref="JbSolutionCacheHash" /> reproduces. Nothing breaks — every feature that needs a name
    ///     switches itself off — and nothing else in the server would ever report it.
    /// </summary>
    NoGenerationMatched,

    /// <summary>
    ///     Nothing was written, because the marker file could not be opened. Swallowed rather than raised,
    ///     and deliberately not <see cref="NoGenerationMatched" />: an unusable cache home says nothing about
    ///     <c>jb</c>'s naming.
    /// </summary>
    NotStamped
}

/// <summary>
///     A file inside the cache home whose modification time records when a <c>jb</c> run against that cache
///     generation last <em>succeeded</em>, and whose content names the generation directory that run left
///     behind and the <c>jb</c> build that left it. The speculative pre-warm reads the timestamp to skip a
///     generation something has already warmed, a transplant reads the name to find a donor worth copying,
///     the cache-state line reads the build to tell a cache this <c>jb</c> can resume from one it will
///     rebuild, and — because every successful run
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
///         The content grew a second line — the <c>jb</c> build that wrote the generation — and the growth is
///         safe in the one direction that cannot be tested from here. A previously released server reads the
///         whole file as one string and asks <see cref="IsBareDirectoryName" /> of it, which passes: a
///         newline is no path separator, so both lines survive the guard as one implausible name. What
///         declines them is the directory lookup behind it — nothing under the cache home is called that —
///         so that build answers null, forgoes the generation as a donor, and pre-warms as if it had never
///         been named. Declining is the direction this file is always allowed to fail in, so an old server
///         meeting a new marker costs work rather than misdirecting a copy. Which of the two checks does it
///         is worth naming: the guard reads like the one holding the line, and it is not.
///     </para>
///     <para>
///         The silence is total, which leaves one thing worth saying out loud with nowhere here to say it:
///         that the derivation this server makes from <c>jb</c>'s directory naming has stopped matching what
///         <c>jb</c> writes. That is not a filesystem failure and nothing else would ever report it, so
///         <see cref="Stamp" /> hands it back as <see cref="StampOutcome.NoGenerationMatched" /> and
///         <see cref="Services.JbRunner" /> is what turns it into a warning said once. Returned rather than
///         logged because "once" has to mean once per server session, and a latch on a static field here
///         would be once per <em>process</em>: exact only while a process holds a single session, and under a
///         parallel test run the first session to see drift absorbs every later session's warning.
///     </para>
/// </remarks>
internal static class JbWarmMarker
{
    private const string Extension = "warm";

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
    ///     Record that a <c>jb</c> run against this cache generation has just succeeded, which generation
    ///     directory it left warm, and which <c>jb</c> build left it that way. The modification time is the
    ///     debounce's whole payload; the content is what lets a <em>different</em> solution find this one,
    ///     since the marker's own file name is a key nothing can invert back into a solution path.
    /// </summary>
    /// <remarks>
    ///     Named from the outside, by matching this solution's computed hash against the directories actually
    ///     on disk, rather than composed from the hash alone: what is wanted is the generation <c>jb</c> just
    ///     used, forks included, and only the filesystem knows which of those exists. Finding none leaves the
    ///     file empty — exactly the marker this used to write — so every reader that wants a name is told
    ///     there is none, and the features built on it switch themselves off rather than acting on a guess.
    ///     The <see cref="StampOutcome" /> is the only account of that; the empty marker a naming drift leaves
    ///     is indistinguishable from the one a failure never wrote.
    ///     <para>
    ///         <paramref name="jbVersion" /> rides beside the name rather than in a sidecar of its own, and is
    ///         optional for the reason the empty marker is: a caller with no build to name writes the one-line
    ///         marker this always wrote, and every reader of the second line then reports the same "written by
    ///         something else" it reports for a marker from an older build — the reading that can only cost
    ///         work.
    ///     </para>
    /// </remarks>
    internal static StampOutcome Stamp(string solutionPath, string cacheHome, ILogger logger, string? jbVersion = null)
    {
        try
        {
            string? generationName = WarmedGenerationName(solutionPath, cacheHome);

            using FileStream marker = JbSidecar.OpenToWrite(solutionPath, cacheHome, Extension);

            // Decided after the open and never before it: a cache home that cannot hold the marker throws on
            // the line above and leaves by the catch, so a broken filesystem is never reported as drift.
            if (generationName is null) return StampOutcome.NoGenerationMatched;

            marker.Write(Encoding.UTF8.GetBytes(Content(generationName, jbVersion)));

            // The mechanism donor discovery depends on, and the one step of it nothing else records: a
            // generation no marker names can never be copied, however warm it is.
            logger.LogDebug(
                "Stamped the jb warm marker for solution {SolutionPath}, naming cache generation {GenerationName}, warmed by {JbVersion}",
                solutionPath,
                generationName,
                jbVersion is null ? "an unnamed jb" : $"jb {jbVersion}");

            return StampOutcome.NamedGeneration;
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            logger.LogDebug(exception, "Could not stamp the jb warm marker for solution {SolutionPath} in cache home {CacheHome}", solutionPath, cacheHome);
            return StampOutcome.NotStamped;
        }
    }

    /// <summary>
    ///     Both facts one marker can hold, off a single read: the generation name, or <see langword="null" />
    ///     when it names none this server should act on, and the <c>jb</c> build that wrote it, or
    ///     <see langword="null" /> when the marker names none. Takes the marker's path rather than a solution
    ///     path because the caller that needs both — donor discovery — is reading <em>another</em> solution's
    ///     marker, has nothing but the file to go on, and should not pay a second read for its second
    ///     question.
    /// </summary>
    /// <remarks>
    ///     Every uncertainty about the name answers <see langword="null" />: a marker written before this
    ///     content existed, an empty one written under naming drift, one whose generation has since been
    ///     deleted, and one whose first line is not a bare directory name at all. The last is a guard rather
    ///     than a formality — the name is combined with a cache home to make a path a caller then copies
    ///     from, so anything carrying a separator, a drive, or a parent reference is refused before it can
    ///     address a directory outside the cache home.
    /// </remarks>
    internal static (string? GenerationName, string? JbVersion) TryReadMarker(
        string markerFilePath,
        string cacheHome,
        ILogger logger)
    {
        try
        {
            IReadOnlyList<string> lines = ReadLines(markerFilePath);

            return (GenerationNameOf(lines, cacheHome), JbVersionOf(lines));
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            logger.LogDebug(exception, "Could not read the jb warm marker {MarkerFilePath}", markerFilePath);
            return (null, null);
        }
    }

    /// <summary>
    ///     The cache generation directory named by the marker file at <paramref name="markerFilePath" />, or
    ///     <see langword="null" /> when it names none this server should act on — the name half of
    ///     <see cref="TryReadMarker" />, for a caller with only that question.
    /// </summary>
    internal static string? TryReadGenerationName(string markerFilePath, string cacheHome, ILogger logger)
    {
        return TryReadMarker(markerFilePath, cacheHome, logger).GenerationName;
    }

    /// <summary>
    ///     The <c>jb</c> build that left this generation warm, or <see langword="null" /> when the marker
    ///     names none — a marker from a build of this server that recorded only the generation, one written
    ///     under naming drift, or one that cannot be read at all.
    /// </summary>
    /// <remarks>
    ///     Takes no cache home because there is nothing to resolve against: unlike the generation name, this
    ///     line addresses no directory, so it needs no guard beyond being reported exactly as it was written.
    ///     Its <see langword="null" /> is not "no opinion" to a caller —
    ///     <see cref="WrittenByAnotherBuild" /> reads it as "written by something other than the build about
    ///     to run", because a cache <c>jb</c> did not write is one it rebuilds in place. That is this file's
    ///     usual direction: every unreadable answer costs work rather than saving it.
    /// </remarks>
    internal static string? TryReadJbVersion(string markerFilePath, ILogger logger)
    {
        try
        {
            return JbVersionOf(ReadLines(markerFilePath));
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            logger.LogDebug(exception, "Could not read the jb build recorded in the warm marker {MarkerFilePath}", markerFilePath);
            return null;
        }
    }

    /// <summary>
    ///     Whether a cache is vouched for by a <c>jb</c> build other than <paramref name="currentJbVersion" />
    ///     — a marker naming another build, and a marker naming none at all, which is every marker written
    ///     before this server recorded one. The one spelling of the staleness judgement, shared by the
    ///     cache-state line and by donor selection so the log cannot promise a rebuild the transplanter
    ///     ignores, or the other way round.
    /// </summary>
    /// <remarks>
    ///     A <see langword="null" /> current build is the judgement's off switch: with nothing to compare
    ///     against, nothing reads as stale, rather than a server that cannot name its own <c>jb</c> calling
    ///     every cache stale for ever. Ordinal equality, because a <c>jb</c> version is an identifier rather
    ///     than an ordering: <c>2026.2.1</c> and <c>2026.2.0.2</c> only ever have to be told apart.
    /// </remarks>
    internal static bool WrittenByAnotherBuild(string? markerJbVersion, string? currentJbVersion)
    {
        return currentJbVersion is not null && !string.Equals(markerJbVersion, currentJbVersion, StringComparison.Ordinal);
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
    ///     What one marker holds: the generation name alone, or the name and the <c>jb</c> build that wrote
    ///     it, one per line. Composed here and parsed by <see cref="ReadLines" /> so the two cannot drift.
    /// </summary>
    private static string Content(string generationName, string? jbVersion)
    {
        return string.IsNullOrWhiteSpace(jbVersion) ? generationName : $"{generationName}\n{jbVersion}";
    }

    /// <summary>
    ///     The marker's lines, trimmed, in order — the shared parse behind every content reader here. A
    ///     marker written by any build of this server is a few dozen bytes, so reading it whole costs nothing
    ///     and keeps each reader a question about one line rather than about a file format. The open, and the
    ///     absent-file-means-nothing-recorded split, are <see cref="JbSidecar.ReadLines" />'s; the trim is
    ///     this artifact's own judgement, since its lines are compared ordinally and a stray space would fail
    ///     them.
    /// </summary>
    private static IReadOnlyList<string> ReadLines(string markerFilePath)
    {
        return JbSidecar.ReadLines(markerFilePath).Select(line => line.Trim()).ToList();
    }

    /// <summary>
    ///     The generation name <paramref name="lines" /> carry, refused unless it is a bare directory name
    ///     actually on disk under <paramref name="cacheHome" /> — see <see cref="TryReadMarker" /> for why
    ///     every uncertainty answers <see langword="null" />.
    /// </summary>
    private static string? GenerationNameOf(IReadOnlyList<string> lines, string cacheHome)
    {
        if (lines is not [{ } name, ..]) return null;

        if (!IsBareDirectoryName(name)) return null;

        return Directory.Exists(JbCacheGenerations.PathUnder(cacheHome, name)) ? name : null;
    }

    /// <summary>The <c>jb</c> build <paramref name="lines" /> name, or <see langword="null" /> for the one-line marker.</summary>
    private static string? JbVersionOf(IReadOnlyList<string> lines)
    {
        return lines is [_, { Length: > 0 } version, ..] ? version : null;
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