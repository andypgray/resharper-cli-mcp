namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Represents one ReSharper cache generation directory under a cache home.
/// </summary>
/// <param name="Hash">
///     The digits <c>jb</c> derives from the solution's path and nothing else, so the hash is identity:
///     directories sharing it are generations of one solution, and two values under one solution file name
///     are two different solutions.
/// </param>
/// <param name="FullPath">The directory's absolute path.</param>
internal sealed record JbCacheGeneration(string Hash, string FullPath)
{
    internal string Name => Path.GetFileName(FullPath);
}

/// <summary>
///     Represents the generations under one cache home that share one solution's file name, split by
///     ownership.
/// </summary>
/// <remarks>Both lists keep <see cref="JbCacheGenerations.Find" />'s name order.</remarks>
/// <param name="Owned">The ones the hash proves are this solution path's own.</param>
/// <param name="Neighbours">
///     The rest: same solution file name, different path — another checkout or copy of the repository.
/// </param>
internal sealed record JbSolutionGenerations(
    IReadOnlyList<JbCacheGeneration> Owned,
    IReadOnlyList<JbCacheGeneration> Neighbours);

/// <summary>
///     Reads the cache home's directory layout from the outside: <c>jb</c> stores a solution's analysis in
///     <c>_{solution file name without extension}.{hash}.{generation}</c> directly under the cache home, and
///     forks a further <c>.{generation}</c> when it cannot open the one it wanted (see <see cref="JbRunLock" />).
/// </summary>
/// <remarks>
///     None of that naming is documented by JetBrains, so it is matched structurally and strictly, and
///     anything unaccounted for is skipped rather than assumed. A cache home shared by a family of solutions
///     holds <c>_App.100200300.00</c> beside <c>_App.Core.400500600.00</c>, and the only thing separating
///     solution <c>App</c>'s cache from its sibling's is that the second's remainder does not parse as
///     <c>{hash}.{generation}</c>, so a looser prefix match would delete another solution's cache.
/// </remarks>
internal static class JbCacheGenerations
{
    /// <summary>
    ///     Gets how two cache-home directory names are compared, matching how <see cref="JbSidecar" /> folds the
    ///     paths that key the same directories.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The operating system stands in for the cache home's filesystem, which is what decides the
    ///         answer. The proxy is right on NTFS and on ext4, and wrong in both directions elsewhere: macOS's
    ///         default APFS volume is case-insensitive and gets <see cref="StringComparison.Ordinal" />, while a
    ///         Windows directory marked with <c>setCaseSensitiveInfo</c> and a case-insensitive Linux mount get
    ///         <see cref="StringComparison.OrdinalIgnoreCase" />.
    ///     </para>
    ///     <para>
    ///         Reaching the gap takes one solution path spelt in two cases across runs on a filesystem that
    ///         reads both as one directory. This server does not produce that on its own: it hands <c>jb</c>
    ///         the path it resolved, and discovery reads the on-disk spelling back. It takes a hand-typed
    ///         <c>solutionPath</c> argument or <c>JB_SOLUTION_PATH</c> in another case, or a <c>jb</c> run
    ///         made outside this server.
    ///     </para>
    ///     <para>
    ///         A misread makes this server stop recognising a generation as its own, and every consequence
    ///         points away from deleting: a reset declines the generation, a transplant misses a donor, a clean
    ///         run stamps <see cref="StampOutcome.NoGenerationMatched" /> and warns of naming drift falsely, and
    ///         the cache-state line opens a warm run as cold. Nothing is wrongly deleted. The one consequence
    ///         that is not a missed optimisation is the lock key: <see cref="JbSidecar" /> folds case by this
    ///         same switch, so the second spelling derives a second key, and two sessions each holding a lock
    ///         over one generation fork it.
    ///     </para>
    /// </remarks>
    internal static StringComparison NameComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    ///     Finds the generations under <paramref name="cacheHome" /> built from a solution file named
    ///     <paramref name="solutionName" /> (no extension), ordered by name.
    /// </summary>
    /// <remarks>
    ///     A cache home that does not exist yet holds nothing; any other enumeration failure is the caller's to
    ///     report, because a caller that is about to delete these must not mistake "could not look" for
    ///     "nothing there".
    /// </remarks>
    internal static List<JbCacheGeneration> Find(string cacheHome, string solutionName)
    {
        if (!Directory.Exists(cacheHome)) return [];

        List<JbCacheGeneration> generations = [];
        foreach (string fullPath in Directory.EnumerateDirectories(cacheHome))
        {
            if (MatchHash(Path.GetFileName(fullPath), solutionName) is not { } hash) continue;

            generations.Add(new JbCacheGeneration(hash, fullPath));
        }

        return generations.OrderBy(generation => generation.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    ///     Finds the generations built from a solution file named like <paramref name="solutionPath" />'s, split
    ///     by ownership.
    /// </summary>
    /// <remarks>
    ///     Which of the same-named generations are the solution's own is decided by reproducing <c>jb</c>'s
    ///     hash of the path (<see cref="JbSolutionCacheHash" />), so ownership is proved rather than guessed: a
    ///     hash matching nothing owns nothing. This pairing of name derivation, hash and comparison is the one
    ///     safety-critical predicate of the cache tools, which is why it lives beside the parser rather than
    ///     being restated at each call site.
    /// </remarks>
    internal static JbSolutionGenerations FindFor(string cacheHome, string solutionPath)
    {
        string hash = JbSolutionCacheHash.Compute(solutionPath);
        List<JbCacheGeneration> generations = Find(cacheHome, Path.GetFileNameWithoutExtension(solutionPath));

        List<JbCacheGeneration> owned = generations.Where(generation => SameHash(generation.Hash, hash)).ToList();
        List<JbCacheGeneration> neighbours = generations.Where(generation => !SameHash(generation.Hash, hash)).ToList();
        return new JbSolutionGenerations(owned, neighbours);
    }

    /// <summary>
    ///     Returns where the generation directory called <paramref name="generationName" /> sits under
    ///     <paramref name="cacheHome" />, with the home resolved absolute so every caller spells one path the
    ///     same way.
    /// </summary>
    /// <remarks>
    ///     This composition constrains nothing, and a caller must not read it as though it did:
    ///     <see cref="Path.Combine(string, string)" /> lets <c>..</c> climb out of the home and discards the
    ///     home outright for a rooted second argument. A name that was not enumerated from the cache home or
    ///     composed by this server, such as a warm marker's content, must pass
    ///     <c>JbWarmMarker.IsBareDirectoryName</c> before it reaches here.
    /// </remarks>
    internal static string PathUnder(string cacheHome, string generationName)
    {
        return Path.Combine(Path.GetFullPath(cacheHome), generationName);
    }

    /// <summary>
    ///     Determines whether <paramref name="directoryName" /> names a generation of a solution file called
    ///     like <paramref name="solutionPath" />'s but built from a different path: the shape a transplant donor
    ///     has.
    /// </summary>
    /// <remarks>
    ///     A generation of this very solution fails it, and so does anything else in the cache home, which is
    ///     another solution entirely.
    /// </remarks>
    internal static bool IsNeighbourOf(string directoryName, string solutionPath)
    {
        string? hash = MatchHash(directoryName, Path.GetFileNameWithoutExtension(solutionPath));
        return hash is not null && !SameHash(hash, JbSolutionCacheHash.Compute(solutionPath));
    }

    private static bool SameHash(string left, string right)
    {
        return string.Equals(left, right, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Returns the hash in <paramref name="directoryName" /> when it names a cache generation of a solution
    ///     file called <paramref name="solutionName" />, else <see langword="null" />.
    /// </summary>
    /// <remarks>
    ///     The remainder after the solution name must be exactly <c>{hash}.{generation}</c>: an optionally
    ///     negative integer and a non-negative one.
    /// </remarks>
    internal static string? MatchHash(string directoryName, string solutionName)
    {
        var prefix = $"_{solutionName}.";
        if (!directoryName.StartsWith(prefix, NameComparison)) return null;

        string remainder = directoryName[prefix.Length..];

        int separator = remainder.LastIndexOf('.');
        if (separator <= 0) return null;

        string hash = remainder[..separator];
        string generation = remainder[(separator + 1)..];

        return IsInteger(hash) && IsDigits(generation) ? hash : null;
    }

    /// <summary>
    ///     Determines whether <paramref name="value" /> is an integer, negative included, since <c>jb</c>'s hash
    ///     is signed and often negative.
    /// </summary>
    private static bool IsInteger(string value)
    {
        return IsDigits(value.StartsWith('-') ? value[1..] : value);
    }

    private static bool IsDigits(string value)
    {
        return value.Length > 0 && value.All(char.IsAsciiDigit);
    }
}