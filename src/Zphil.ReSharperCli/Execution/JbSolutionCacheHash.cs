using System.Globalization;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Reproduces the hash <c>jb</c> puts in a cache generation's directory name, so this server can say
///     which of the generations under a cache home belongs to the solution it was pointed at.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="JbCacheGenerations" /> reads those names from the outside and can only group them:
///         same hash means same solution, two hashes under one solution <em>file name</em> mean two
///         solutions. Which is which it cannot say, because the name records a hash and never a path. This
///         closes that gap from the other end — compute the hash the same way and the answer is a
///         comparison rather than a guess.
///     </para>
///     <para>
///         None of it is documented, and it is a derivation of someone else's private naming scheme rather
///         than a contract, so every caller must treat a mismatch as ordinary: a computed hash that matches
///         no directory means "nothing here is provably ours", never "delete the closest thing". Drift in a
///         future <c>jb</c> then costs a skipped optimisation or a reset that drops nothing — no wrong
///         directory is ever picked, because a wrong hash matches nothing at all.
///     </para>
/// </remarks>
internal static class JbSolutionCacheHash
{
    private const int Seed = 19;
    private const int Multiplier = 31;

    /// <summary>
    ///     The hash for <paramref name="solutionPath" />, rendered exactly as it appears in a directory
    ///     name. Takes the absolute path as <c>ResolvedConfig</c> resolved it and hands it to <c>jb</c> —
    ///     the same string on both sides is the point, so nothing is normalised here beyond the case fold
    ///     below.
    /// </summary>
    internal static string Compute(string solutionPath)
    {
        int hash = Seed;
        foreach (char character in solutionPath) hash = unchecked(hash * Multiplier + FoldCase(character));

        return hash.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     What the first generation directory for <paramref name="solutionPath" /> is called: the same
    ///     <c>_{solution file name}.{hash}.{generation}</c> shape <see cref="JbCacheGenerations" /> parses,
    ///     at generation <c>00</c>. Only generation <c>00</c> is ever composed here — the higher ones are
    ///     forks <c>jb</c> makes when it cannot open the one it wanted, which is not something to create on
    ///     its behalf.
    /// </summary>
    internal static string FirstGenerationDirectoryName(string solutionPath)
    {
        string solutionName = Path.GetFileNameWithoutExtension(solutionPath);
        return $"_{solutionName}.{Compute(solutionPath)}.00";
    }

    /// <summary>
    ///     Lower-cases a character the way the hash does: ASCII <c>A</c>–<c>Z</c> and nothing else. The
    ///     restraint is load-bearing rather than an optimisation. A blanket <c>| 0x20</c> would fold the
    ///     path separator itself (<c>\</c> becomes <c>|</c>) and was ruled out against real directory names,
    ///     and a culture-aware <see cref="char.ToLowerInvariant" /> would fold non-ASCII letters this hash
    ///     leaves alone — either one silently produces a hash matching no directory on disk.
    /// </summary>
    private static char FoldCase(char character)
    {
        return character is >= 'A' and <= 'Z' ? (char)(character + 32) : character;
    }
}