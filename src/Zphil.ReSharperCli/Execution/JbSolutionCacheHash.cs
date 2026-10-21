using System.Globalization;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Reproduces the hash <c>jb</c> puts in a cache generation's directory name, so this server can say
///     which of the generations under a cache home belongs to the solution it was pointed at.
/// </summary>
/// <remarks>
///     None of it is documented, and it is a derivation of someone else's private naming scheme rather than a
///     contract, so every caller must treat a mismatch as ordinary: a computed hash that matches no directory
///     means "nothing here is known to be ours", never "delete the closest thing". Drift in a future <c>jb</c>
///     then costs a skipped optimisation or a reset that drops nothing, and no wrong directory is ever picked,
///     because a wrong hash matches nothing at all.
/// </remarks>
internal static class JbSolutionCacheHash
{
    private const int Seed = 19;
    private const int Multiplier = 31;

    /// <summary>
    ///     Computes the hash for <paramref name="solutionPath" />, rendered exactly as it appears in a directory
    ///     name.
    /// </summary>
    /// <remarks>
    ///     Takes the absolute path exactly as <c>ResolvedConfig</c> resolved it, the string this server hands
    ///     <c>jb</c>: the hash has to be computed over the same string on both sides, so nothing is normalised
    ///     here beyond the case fold.
    /// </remarks>
    internal static string Compute(string solutionPath)
    {
        int hash = Seed;
        foreach (char character in solutionPath) hash = unchecked(hash * Multiplier + FoldCase(character));

        return hash.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Returns the name of the first generation directory for <paramref name="solutionPath" />: the
    ///     <c>_{solution file name}.{hash}.{generation}</c> shape <see cref="JbCacheGenerations" /> parses, at
    ///     generation <c>00</c>.
    /// </summary>
    /// <remarks>
    ///     Only generation <c>00</c> is composed: the higher ones are forks <c>jb</c> makes when it cannot open
    ///     the one it wanted, and are not this server's to create.
    /// </remarks>
    internal static string FirstGenerationDirectoryName(string solutionPath)
    {
        string solutionName = Path.GetFileNameWithoutExtension(solutionPath);
        return $"_{solutionName}.{Compute(solutionPath)}.00";
    }

    /// <summary>
    ///     Lower-cases a character the way the hash does: ASCII <c>A</c>–<c>Z</c> and nothing else.
    /// </summary>
    /// <remarks>
    ///     A blanket <c>| 0x20</c> would fold the path separator itself (<c>\</c> becomes <c>|</c>), and a
    ///     Unicode-aware <see cref="char.ToLowerInvariant" /> would fold non-ASCII letters this hash leaves
    ///     alone; either one silently produces a hash matching no directory on disk.
    /// </remarks>
    private static char FoldCase(char character)
    {
        return character is >= 'A' and <= 'Z' ? (char)(character + 32) : character;
    }
}