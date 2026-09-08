namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     One ReSharper cache generation directory sitting under a cache home.
/// </summary>
/// <param name="Hash">
///     The opaque digits <c>jb</c> derives from the solution's <em>path</em> — verified: re-running one
///     solution with a different argument shape reuses its hash, while a same-named solution in another
///     directory gets a new one. So the hash is identity: directories sharing it are generations of one
///     solution, and two values under one solution <em>file name</em> are two different solutions.
/// </param>
/// <param name="FullPath">The absolute path, which is what a delete takes.</param>
internal sealed record JbCacheGeneration(string Hash, string FullPath)
{
    /// <summary>The directory's own name, which is what a report names.</summary>
    internal string Name => Path.GetFileName(FullPath);
}

/// <summary>
///     The generations under one cache home sharing one solution's file name, split by ownership. Both lists
///     keep <see cref="JbCacheGenerations.Find" />'s name order.
/// </summary>
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
///     anything unaccounted for is skipped rather than assumed. The strictness is load-bearing rather than
///     defensive, and the case is a real one: a cache home shared by a family of solutions holds
///     <c>_App.100200300.00</c> beside <c>_App.Core.400500600.00</c>, and the only thing separating
///     solution <c>App</c>'s cache from its sibling's is that the second's remainder does not parse as
///     <c>{hash}.{generation}</c>. A looser prefix match would delete another solution's cache.
/// </remarks>
internal static class JbCacheGenerations
{
    /// <summary>
    ///     How two cache-home directory names are compared, matching <see cref="JbRunLock" />'s treatment of
    ///     the paths that key the same directories. Internal because a transplant that has just replaced one
    ///     generation tells it apart from the forks it is sweeping up by name, and there is one right answer
    ///     to how these names compare.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The operating system stands in for the cache home's filesystem, which is what actually decides
    ///         the answer. The proxy is right on NTFS and on ext4, and wrong in both directions: macOS's
    ///         default APFS volume is case-insensitive and gets <see cref="StringComparison.Ordinal" />, while
    ///         a Windows directory marked with <c>setCaseSensitiveInfo</c> and a case-insensitive Linux mount
    ///         get <see cref="StringComparison.OrdinalIgnoreCase" />.
    ///     </para>
    ///     <para>
    ///         Reaching the gap takes one solution path spelt in two cases across runs on a filesystem that
    ///         reads both as one directory. This server never produces that on its own: it hands <c>jb</c>
    ///         the path it resolved, and discovery reads the on-disk spelling back. It takes a hand-typed
    ///         <c>solutionPath</c> argument or <c>JB_SOLUTION_PATH</c> in another case, or a <c>jb</c> run
    ///         made outside this server.
    ///     </para>
    ///     <para>
    ///         What that costs is a generation this server stops recognising as its own, and every
    ///         consequence points away from deleting: a reset declines its own generation, a transplant
    ///         misses a donor, a clean run stamps <see cref="StampOutcome.NoGenerationMatched" /> and raises
    ///         the once-per-session naming-drift warning falsely, and the cache-state line opens a warm run
    ///         as cold. Nothing is ever wrongly deleted. The one consequence that is not a missed
    ///         optimisation is the lock key: <c>JbSidecar</c> folds case by this same switch, so the second
    ///         spelling derives a second key, and two sessions each holding a lock over one generation fork
    ///         it.
    ///     </para>
    ///     <para>
    ///         It stays because the only correct fix is probing the filesystem rather than picking a better
    ///         switch, and one probe would not be enough: the lock key covers paths on the <em>solution's</em>
    ///         filesystem, not the cache home's, so the two can disagree. There is no macOS machine here to
    ///         prove either against, and macOS is already the platform whose weaker guarantees are named
    ///         rather than assumed — <see cref="ChildProcessLifetime" /> has no orphan primitive there.
    ///         Revisit on a report from one.
    ///     </para>
    /// </remarks>
    internal static StringComparison NameComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    ///     The generations under <paramref name="cacheHome" /> built from a solution file named
    ///     <paramref name="solutionName" /> (no extension), ordered by name. A cache home that does not exist
    ///     yet holds nothing; any other enumeration failure is the caller's to report, because a caller that
    ///     is about to delete these must not mistake "could not look" for "nothing there".
    /// </summary>
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
    ///     The generations built from a solution file named like <paramref name="solutionPath" />'s, split by
    ///     ownership. Which of the same-named generations are the solution's own is decided by reproducing
    ///     <c>jb</c>'s hash of the path (<see cref="JbSolutionCacheHash" />), so ownership is proved rather
    ///     than guessed — a hash matching nothing owns nothing, and every caller treats that as "leave
    ///     everything alone" rather than "pick the closest". This pairing of name derivation, hash, and
    ///     comparison is the one safety-critical predicate of the cache tools, which is why it lives here
    ///     beside the parser instead of being restated at each call site.
    /// </summary>
    internal static JbSolutionGenerations FindFor(string cacheHome, string solutionPath)
    {
        string hash = JbSolutionCacheHash.Compute(solutionPath);
        List<JbCacheGeneration> generations = Find(cacheHome, Path.GetFileNameWithoutExtension(solutionPath));

        List<JbCacheGeneration> owned = generations.Where(generation => SameHash(generation.Hash, hash)).ToList();
        List<JbCacheGeneration> neighbours = generations.Where(generation => !SameHash(generation.Hash, hash)).ToList();
        return new JbSolutionGenerations(owned, neighbours);
    }

    /// <summary>
    ///     Where the generation directory called <paramref name="generationName" /> sits under
    ///     <paramref name="cacheHome" />, with the home resolved absolute so callers all spell one path the
    ///     same way. Lives beside the parser because the name can come from a warm marker's content —
    ///     untrusted input to a copy.
    /// </summary>
    /// <remarks>
    ///     This composition constrains nothing on its own, and a caller must not read it as though it did:
    ///     <see cref="Path.Combine(string, string)" /> lets <c>..</c> climb out of the home and discards the
    ///     home outright for a rooted second argument. What keeps the untrusted case inside the cache home is
    ///     <c>JbWarmMarker.IsBareDirectoryName</c>, which rejects any marker content that is not a lone
    ///     directory name before this is ever called. Every other caller passes a name it enumerated from the
    ///     cache home or composed itself. A new caller taking a name from anywhere else owes the same check.
    /// </remarks>
    internal static string PathUnder(string cacheHome, string generationName)
    {
        return Path.Combine(Path.GetFullPath(cacheHome), generationName);
    }

    /// <summary>
    ///     Whether <paramref name="directoryName" /> names a generation of a solution file called like
    ///     <paramref name="solutionPath" />'s but built from a <em>different</em> path — the shape a
    ///     transplant donor has. A generation of this very solution fails it, and so does anything else in
    ///     the cache home, which is another solution entirely.
    /// </summary>
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
    ///     The hash in <paramref name="directoryName" /> when it names a cache generation of a solution file
    ///     called <paramref name="solutionName" />, else <see langword="null" />. The remainder after the
    ///     solution name must be exactly <c>{hash}.{generation}</c> — an optionally negative integer and a
    ///     non-negative one — which is what stops a longer solution name's directory matching a shorter one's.
    /// </summary>
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

    /// <summary>An optionally negative integer: jb's hash is a signed value, and negatives are common.</summary>
    private static bool IsInteger(string value)
    {
        return IsDigits(value.StartsWith('-') ? value[1..] : value);
    }

    private static bool IsDigits(string value)
    {
        return value.Length > 0 && value.All(char.IsAsciiDigit);
    }
}