using System.Text;
using Microsoft.Extensions.Logging;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Represents what <see cref="JbWarmMarker.Stamp" /> managed to record: the one thing about a stamp its
///     caller cannot read back off the marker afterwards.
/// </summary>
/// <remarks>
///     <see cref="NoGenerationMatched" /> is kept apart from <see cref="NotStamped" /> because the two mean
///     opposite things: the first is a fact about <c>jb</c>'s naming worth reporting, the second an ordinary
///     filesystem failure that wrote nothing, and folded into one answer an unwritable cache home would accuse
///     <c>jb</c> of drift. Drift is returned rather than logged so the caller can warn about it once per server
///     session, which a static latch in this class could only make once per process.
/// </remarks>
internal enum StampOutcome
{
    /// <summary>The marker was written and names the cache generation the run left warm.</summary>
    NamedGeneration,

    /// <summary>
    ///     The marker was written without a generation name, because no directory under the cache home carries
    ///     this solution's computed hash: <c>jb</c>'s naming has moved away from what
    ///     <see cref="JbSolutionCacheHash" /> reproduces.
    /// </summary>
    NoGenerationMatched,

    /// <summary>
    ///     Nothing was written, because the cache home could not be read or the marker could not be written.
    /// </summary>
    NotStamped
}

/// <summary>
///     Represents everything one warm marker records: the cache generation directory the run left warm, or
///     <see langword="null" /> when the marker names none this server should act on; the <c>jb</c> build
///     that wrote it; and the solution path it was run against.
/// </summary>
/// <remarks>
///     <see cref="SolutionPath" /> is for display and existence checks only, and is never combined into a
///     directory to copy from or delete: <c>JbWarmMarker.IsBareDirectoryName</c> constrains
///     <see cref="GenerationName" />, and nothing constrains the solution path.
/// </remarks>
internal sealed record WarmMarkerContent(string? GenerationName, string? JbVersion, string? SolutionPath);

/// <summary>
///     Provides the warm marker: a file in the cache home whose modification time records when a <c>jb</c> run
///     against one cache generation last succeeded, and whose content is a <see cref="WarmMarkerContent" />.
/// </summary>
/// <remarks>
///     <para>
///         Every successful <c>jb</c> run stamps it, so its existence alone distinguishes a cache some run
///         produced from the part-built remnant of one that never finished. It is a file of its own rather
///         than the <see cref="JbRunLock" /> lock file's timestamp, which moves when a run starts and again
///         when one fails, while freshness turns on when one last succeeded.
///     </para>
///     <para>
///         Every failure is swallowed at <c>Debug</c> and reads as the answer that can only cost work: a
///         reader can permit a redundant pre-warm or forgo a copy, never suppress the one or misdirect the
///         other. <see cref="Exists" /> fails the other way, towards protecting a cache, because its caller's
///         only use for <see langword="false" /> is to delete.
///     </para>
///     <para>
///         An older build of this server sharing the cache home reads the whole file as a single name, which
///         matches no directory it would accept, so it declines the generation as a donor rather than
///         misdirecting a copy. A current reader takes the lines positionally and ignores any past the third.
///     </para>
/// </remarks>
internal static class JbWarmMarker
{
    private const string Extension = "warm";

    internal static string PathFor(string solutionPath, string cacheHome)
    {
        return JbSidecar.PathFor(solutionPath, cacheHome, Extension);
    }

    /// <summary>
    ///     Finds every warm marker under <paramref name="cacheHome" />, with the key its file name carries.
    /// </summary>
    internal static IEnumerable<(string Key, string MarkerPath)> FindAll(string cacheHome)
    {
        return JbSidecar.FindAll(cacheHome, Extension);
    }

    /// <summary>
    ///     Records that a <c>jb</c> run against this cache generation has just succeeded, which generation
    ///     directory it left warm, and which <c>jb</c> build left it that way.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The generation name is what lets a different solution find this one, since the marker's file name
    ///         is a one-way key (<see cref="JbSidecar.ComputeKey" />). It is read off the directories on disk
    ///         rather than composed from the hash, because the generation wanted is the one <c>jb</c> just used,
    ///         forks included. Finding none leaves the first line empty, so every reader that wants a name finds
    ///         none and the features built on it switch off rather than act on a guess.
    ///     </para>
    ///     <para>
    ///         The build and the solution are written even then, because a marker that records no build reads as
    ///         another build's (<see cref="WrittenByAnotherBuild" />), and a drifted marker would then send the
    ///         pre-warm to analyse the solution at every session start. <paramref name="jbVersion" /> is optional
    ///         for a caller with no build to name, whose marker reads the same way: the reading that can only
    ///         cost work.
    ///     </para>
    /// </remarks>
    internal static StampOutcome Stamp(string solutionPath, string cacheHome, ILogger logger, string? jbVersion = null)
    {
        try
        {
            string? generationName = WarmedGenerationName(solutionPath, cacheHome);

            using FileStream marker = JbSidecar.OpenToWrite(solutionPath, cacheHome, Extension);

            marker.Write(Encoding.UTF8.GetBytes(Content(generationName, jbVersion, solutionPath)));

            // Decided after the write and never before it: a cache home that cannot hold the marker throws
            // above and leaves by the catch, so a broken filesystem is never reported as drift.
            if (generationName is null) return StampOutcome.NoGenerationMatched;

            // Logged because donor discovery depends on this step: a generation no marker names is never
            // copied, however warm it is.
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
    ///     Reads every fact one marker holds in a single read.
    /// </summary>
    /// <remarks>
    ///     Takes the marker's path rather than a solution path because a caller reading another solution's
    ///     marker has nothing but the file to go on, and should not pay a read per question. Every uncertainty
    ///     about the name answers <see langword="null" />: an empty or missing first line, a generation since
    ///     deleted, and a first line that is not a bare directory name (<see cref="IsBareDirectoryName" />).
    /// </remarks>
    internal static WarmMarkerContent TryReadMarker(
        string markerFilePath,
        string cacheHome,
        ILogger logger)
    {
        try
        {
            IReadOnlyList<string> lines = ReadLines(markerFilePath);

            return new WarmMarkerContent(GenerationNameOf(lines, cacheHome), JbVersionOf(lines), SolutionPathOf(lines));
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            logger.LogDebug(exception, "Could not read the jb warm marker {MarkerFilePath}", markerFilePath);
            return new WarmMarkerContent(null, null, null);
        }
    }

    /// <summary>
    ///     Maps every generation directory a marker under <paramref name="cacheHome" /> names to the solution
    ///     path that marker records, or to <see langword="null" /> where it records none.
    /// </summary>
    /// <remarks>
    ///     A generation's name records a hash and never a path, so a marker is the only record of whose it is.
    ///     Absent and present-with-null are different answers and the caller is owed both: a generation missing
    ///     from the map had no successful run through this server stamp it at all, while one mapped to null was
    ///     stamped by a build that recorded no path and will name itself after the next clean run. Two markers
    ///     naming one directory, reachable through the case scenario
    ///     <see cref="JbCacheGenerations.NameComparison" /> describes, resolve to the first: an attribution is a
    ///     sentence in a report, so a tie is not worth a policy. A filesystem failure leaves the map holding
    ///     whatever was read before it, since a report that cannot attribute is still a correct report.
    /// </remarks>
    internal static Dictionary<string, string?> FindRecordedSolutionPaths(string cacheHome, ILogger logger)
    {
        Dictionary<string, string?> recorded = new(StringComparer.FromComparison(JbCacheGenerations.NameComparison));

        try
        {
            foreach ((string _, string markerPath) in FindAll(cacheHome))
            {
                WarmMarkerContent content = TryReadMarker(markerPath, cacheHome, logger);

                if (content.GenerationName is { } name) recorded.TryAdd(name, content.SolutionPath);
            }
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            logger.LogDebug(exception, "Could not read the jb warm markers under cache home {CacheHome}", cacheHome);
        }

        return recorded;
    }

    /// <summary>
    ///     Returns the <c>jb</c> build that left this generation warm, or <see langword="null" /> when the
    ///     marker names none or cannot be read.
    /// </summary>
    /// <remarks>
    ///     Takes no cache home because, unlike the generation name, this line addresses no directory and needs
    ///     no guard.
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
    ///     Determines whether a cache is vouched for by a <c>jb</c> build other than
    ///     <paramref name="currentJbVersion" />: a marker naming another build, or naming none.
    /// </summary>
    /// <remarks>
    ///     The one spelling of the staleness judgement, so no reader can call a cache stale while another
    ///     treats it as warm. A <see langword="null" /> current build is the judgement's off switch: with
    ///     nothing to compare against, nothing reads as stale, rather than a server that cannot name its own
    ///     <c>jb</c> calling every cache stale for ever. Ordinal equality, because a <c>jb</c> version is an
    ///     identifier rather than an ordering: <c>2026.2.1</c> and <c>2026.2.0.2</c> only ever have to be told
    ///     apart.
    /// </remarks>
    internal static bool WrittenByAnotherBuild(string? markerJbVersion, string? currentJbVersion)
    {
        return currentJbVersion is not null && !string.Equals(markerJbVersion, currentJbVersion, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Forgets that a run against this cache generation ever succeeded, once the generation the marker
    ///     describes has been dropped and the claim is untrue.
    /// </summary>
    /// <remarks>
    ///     A marker that will not go leaves the dropped cache looking warm, so a pre-warm may be skipped for a
    ///     while.
    /// </remarks>
    internal static void Clear(string solutionPath, string cacheHome, ILogger logger)
    {
        JbSidecar.TryDelete(solutionPath, cacheHome, Extension, "jb warm marker", logger);
    }

    /// <summary>
    ///     Returns how long ago a run against this cache generation last succeeded, or
    ///     <see langword="null" /> when no marker is there or it cannot be read.
    /// </summary>
    /// <remarks>
    ///     The one mtime read behind every freshness judgement, so "recently warm" and "warm, and this old"
    ///     cannot come to disagree. A negative span, from a moved clock or a cache home copied between
    ///     machines, is passed through rather than hidden, for each caller to judge.
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
    ///     Determines whether a run against this cache generation succeeded within <paramref name="window" />.
    /// </summary>
    /// <remarks>
    ///     A missing or unreadable marker reads as stale, and so does a future-dated one, so a marker dated by a
    ///     moved clock or a cache home copied between machines cannot suppress pre-warming forever.
    /// </remarks>
    internal static bool IsFreshWithin(string solutionPath, string cacheHome, TimeSpan window, ILogger logger)
    {
        return Age(solutionPath, cacheHome, logger) is { } age && age >= TimeSpan.Zero && age < window;
    }

    /// <summary>
    ///     Determines whether a <c>jb</c> run against this cache generation has ever succeeded, whenever that
    ///     was.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A marker means some run produced the directories already there and they are the solution's own,
    ///         while no marker means what is on disk is the part-built remnant of a run that never finished.
    ///         Content is not read, so any marker protects, an empty or nameless one included, and markers from
    ///         every build of this server answer the question.
    ///     </para>
    ///     <para>
    ///         Anything that goes wrong answers <see langword="true" />: the caller's only use for
    ///         <see langword="false" /> is to delete a directory, and it must not do that on a question this
    ///         could not answer. <see cref="File.Exists(string)" /> does not throw, so the catch covers a key that
    ///         cannot be derived, from an unusable cache home. It can answer false for a marker present but
    ///         unreadable, which needs no handling here: the caller replaces nothing without first finding a
    ///         donor, and a cache home too broken to read this file hides every donor marker from that search
    ///         too.
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
    ///     Composes the marker's content, one fact per line in <see cref="WarmMarkerContent" />'s order.
    /// </summary>
    /// <remarks>
    ///     A missing name or build is written as an empty line rather than dropped, so the path stays on line
    ///     three: the lines are positional, not keyed.
    /// </remarks>
    private static string Content(string? generationName, string? jbVersion, string solutionPath)
    {
        return $"{generationName}\n{jbVersion?.Trim()}\n{solutionPath}";
    }

    /// <summary>
    ///     Reads the marker's lines, trimmed, in order.
    /// </summary>
    /// <remarks>
    ///     Trimmed because the lines are compared ordinally, and a stray space would fail them.
    /// </remarks>
    private static IReadOnlyList<string> ReadLines(string markerFilePath)
    {
        return JbSidecar.ReadLines(markerFilePath).Select(line => line.Trim()).ToList();
    }

    /// <summary>
    ///     Returns the generation name <paramref name="lines" /> carry, or <see langword="null" /> unless it is
    ///     a bare directory name on disk under <paramref name="cacheHome" />.
    /// </summary>
    private static string? GenerationNameOf(IReadOnlyList<string> lines, string cacheHome)
    {
        if (lines is not [{ } name, ..]) return null;

        if (!IsBareDirectoryName(name)) return null;

        return Directory.Exists(JbCacheGenerations.PathUnder(cacheHome, name)) ? name : null;
    }

    /// <summary>
    ///     Returns the <c>jb</c> build <paramref name="lines" /> name, or <see langword="null" /> when they
    ///     name none.
    /// </summary>
    private static string? JbVersionOf(IReadOnlyList<string> lines)
    {
        return lines is [_, { Length: > 0 } version, ..] ? version : null;
    }

    /// <summary>
    ///     Returns the solution <paramref name="lines" /> record the run against, or <see langword="null" />
    ///     when they record none.
    /// </summary>
    private static string? SolutionPathOf(IReadOnlyList<string> lines)
    {
        return lines is [_, _, { Length: > 0 } solutionPath, ..] ? solutionPath : null;
    }

    /// <summary>
    ///     Returns the generation directory under <paramref name="cacheHome" /> that the run which just
    ///     succeeded left warm: the one whose name carries this solution's computed hash or, where <c>jb</c>
    ///     has forked it, the most recently written, since that is the one it can only just have closed.
    /// </summary>
    /// <remarks>Ties break towards the higher generation number, the later fork.</remarks>
    private static string? WarmedGenerationName(string solutionPath, string cacheHome)
    {
        return JbCacheGenerations.FindFor(cacheHome, solutionPath).Owned
            .OrderByDescending(generation => Directory.GetLastWriteTimeUtc(generation.FullPath))
            .ThenByDescending(generation => generation.Name, StringComparer.Ordinal)
            .Select(generation => generation.Name)
            .FirstOrDefault();
    }

    /// <summary>
    ///     Determines whether <paramref name="content" /> is a directory name and nothing more: no separator,
    ///     no drive, no <c>.</c> or <c>..</c>.
    /// </summary>
    /// <remarks>
    ///     The guard that keeps a marker's content from addressing a directory outside the cache home, since
    ///     the name is combined with the cache home into a path a caller copies from. The equality against
    ///     <see cref="Path.GetFileName(string)" /> rejects every form that carries a path; the dot cases are
    ///     spelled out because they survive it.
    /// </remarks>
    private static bool IsBareDirectoryName(string content)
    {
        if (content.Length == 0 || content is "." or "..") return false;

        return string.Equals(content, Path.GetFileName(content), StringComparison.Ordinal);
    }
}