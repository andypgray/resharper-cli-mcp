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
/// <param name="Name">The directory's own name, which is what a report names.</param>
/// <param name="FullPath">The absolute path, which is what a delete takes.</param>
internal sealed record JbCacheGeneration(string Hash, string Name, string FullPath);

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
    ///     the paths that key the same directories.
    /// </summary>
    private static StringComparison NameComparison =>
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
            string name = Path.GetFileName(fullPath);
            if (MatchHash(name, solutionName) is not { } hash) continue;

            generations.Add(new JbCacheGeneration(hash, name, fullPath));
        }

        return generations.OrderBy(generation => generation.Name, StringComparer.Ordinal).ToList();
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