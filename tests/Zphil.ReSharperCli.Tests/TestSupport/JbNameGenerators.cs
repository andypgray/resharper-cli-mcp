using FsCheck;
using FsCheck.Fluent;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Generators for the names <c>jb</c>'s undocumented cache layout is built from — solution file names,
///     the signed hash it derives from a solution's path, and generation numbers. Each one is constrained to
///     the domain the code under test documents rather than to "any string", because a property whose
///     generator wanders outside that domain proves nothing about the invariant and fails for the wrong
///     reason. Where a known-hostile shape exists (a name carrying dots, a negative hash), it is unioned into
///     the random draw so every seed hits it rather than most seeds missing it.
/// </summary>
internal static class JbNameGenerators
{
    /// <summary>Names that have actually appeared in a cache home, hit on every seed rather than waited for.</summary>
    private static readonly string[] KnownSolutionNames = ["App", "App.Core", "Zphil.ReSharperCli", "a"];

    /// <summary>
    ///     A solution file name without its extension: one to three dot-joined segments of ASCII letters,
    ///     digits, <c>-</c> and <c>_</c>. Dots are the interesting part — they are ordinary in a solution name
    ///     and are also the character the generation-directory scheme separates on, which is the whole reason
    ///     the parser cannot simply split on them.
    /// </summary>
    internal static Gen<string> SolutionName()
    {
        Gen<string> composed = Gen.Choose(1, 3)
            .SelectMany(segmentCount => Segment().ListOf(segmentCount))
            .Select(segments => string.Join('.', segments));

        return Gen.OneOf(composed, Gen.Elements(KnownSolutionNames));
    }

    /// <summary>
    ///     A hash as <c>jb</c> renders it into a directory name: an optionally negative run of one to ten
    ///     ASCII digits. Negative values are about half of the real ones, so they are drawn deliberately
    ///     rather than left to chance.
    /// </summary>
    internal static Gen<string> CacheHash()
    {
        Gen<string> digits = Gen.Choose(1, 10)
            .SelectMany(digitCount => Gen.Elements("0123456789".ToCharArray()).ListOf(digitCount))
            .Select(characters => new string(characters.ToArray()));

        return Gen.Elements("", "-").SelectMany(_ => digits, (sign, value) => sign + value);
    }

    /// <summary>
    ///     A generation number: <c>00</c> for the first, and the higher ones <c>jb</c> forks when it cannot
    ///     open the generation it wanted. Non-negative and zero-padded, matching what appears on disk.
    /// </summary>
    internal static Gen<string> GenerationNumber()
    {
        return Gen.Choose(0, 99).Select(generation => generation.ToString("00"));
    }

    /// <summary>
    ///     A POSIX-shaped absolute path to a solution file. Forward slashes only, so the same generated path
    ///     is a legal argument on both platforms and no property built on it can assert something that is
    ///     only true on one of them.
    /// </summary>
    internal static Gen<string> SolutionPath()
    {
        Gen<List<string>> directories = Gen.Choose(1, 3)
            .SelectMany(depth => Segment().ListOf(depth));

        return directories
            .SelectMany(_ => SolutionName(), (path, name) => (Path: path, Name: name))
            .SelectMany(
                _ => Gen.Elements("slnx", "sln"),
                (solution, extension) =>
                    $"/{string.Join('/', solution.Path)}/{solution.Name}.{extension}");
    }

    /// <summary>
    ///     The directory name <c>jb</c> gives a generation of <paramref name="solutionName" />: the
    ///     <c>_{name}.{hash}.{generation}</c> shape, composed here so a property can hand the parser a name
    ///     that is provably well formed rather than one hand-spelled per test.
    /// </summary>
    internal static Gen<(string DirectoryName, string Hash)> GenerationDirectoryName(string solutionName)
    {
        return CacheHash().SelectMany(
            _ => GenerationNumber(),
            (hash, generation) => ($"_{solutionName}.{hash}.{generation}", hash));
    }

    /// <summary>
    ///     A non-empty run of the characters a solution file name segment is made of.
    /// </summary>
    private static Gen<string> Segment()
    {
        return Gen.Choose(1, 8)
            .SelectMany(length => Gen.Elements("abcXYZ019-_".ToCharArray()).ListOf(length))
            .Select(characters => new string(characters.ToArray()));
    }
}