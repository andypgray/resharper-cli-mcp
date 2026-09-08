using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Shouldly;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     The two halves of <c>jb</c>'s undocumented naming scheme have to agree, and nothing but a test says
///     so: <see cref="JbSolutionCacheHash" /> writes a generation directory's name and
///     <see cref="JbCacheGenerations" /> reads one, in different files, against a scheme neither of them
///     owns. Composing a name and parsing it back is the round trip that holds them together — and it also
///     says that whatever rendering <see cref="JbSolutionCacheHash.Compute" /> produces for a path, the
///     parser accepts it, which is where a negative hash would otherwise slip through.
/// </summary>
/// <remarks>
///     <para>
///         The case-folding properties assert both a collision and its absence, which for a hash would
///         normally take a probabilistic hedge. Here it does not, because the hash is linear and the
///         substitution is a single character. Folding <c>c</c> to <c>f(c)</c> and accumulating
///         <c>hash = hash * 31 + f(c)</c> from a seed of 19 leaves
///         <c>19 * 31^n + Σ f(cᵢ) * 31^(n − 1 − i)</c> in <c>unchecked</c> 32-bit arithmetic, so two strings
///         of equal length differing only at index <c>k</c> differ by <c>(f(a) − f(b)) * 31^(n − 1 − k)</c>
///         modulo 2³². A power of 31 is odd and so invertible modulo 2³², and the difference of two folded
///         UTF-16 code units is smaller than 2³² in absolute value, so that product vanishes exactly when
///         <c>f(a) = f(b)</c>. A one-character substitution therefore collides if and only if the two
///         characters fold alike — which is what licenses asserting that two hashes <em>differ</em>.
///     </para>
///     <para>
///         What folds alike is the second half, and it is the invariant worth guarding: <c>f</c> lower-cases
///         <c>A</c>–<c>Z</c> and touches nothing else. A blanket <c>| 0x20</c> would fold a path separator
///         into <c>|</c>, and <see cref="char.ToLowerInvariant(char)" /> would fold <c>Ö</c>, the Kelvin sign
///         and <c>İ</c>. Either one derives a hash matching no directory on disk, which reads as a solution
///         owning none of its own cache.
///     </para>
/// </remarks>
public sealed class JbSolutionCacheHashPropertyTests
{
    /// <summary>
    ///     Paths carrying the characters the fold has to leave alone: every ASCII letter in both cases, both
    ///     separators, and cased letters from outside ASCII — <c>ß</c> and the dotted and dotless <c>i</c>
    ///     because they are where culture-aware casing misbehaves, the Kelvin sign because
    ///     <see cref="char.ToLowerInvariant(char)" /> maps it onto plain <c>k</c>.
    /// </summary>
    private static readonly string[] CuratedPaths =
    [
        "C:\\Users\\dev\\ABCDEFGHIJKLMNOPQRSTUVWXYZ\\abcdefghijklmnopqrstuvwxyz\\0123456789\\App.slnx",
        "C:\\Users\\dev\\ö Ö ß İ ı Σ σ \u212A\\App.slnx",
        "C:/Users/dev/repo\\Mixed.Separators/App.sln",
        "/home/dev/repo/App.slnx"
    ];

    /// <summary>The characters most likely to expose a fold wider than ASCII <c>A</c>–<c>Z</c>.</summary>
    private static readonly char[] TrapCharacters =
    [
        '[', '{', ']', '}', '\\', '|', '^', '~', '@', '`',
        'Ö', 'ö', 'İ', 'i', 'ı', '\u212A', 'k', 'Σ', 'σ', 'É', 'é', 'ß',
        'A', 'a', 'Z', 'z', 'M', 'm', '/', '.', '0'
    ];

    [Property]
    public Property FirstGenerationDirectoryName_ParsedBack_YieldsThatPathsComputedHash()
    {
        return Prop.ForAll(
            JbNameGenerators.SolutionPath().ToArbitrary(),
            solutionPath =>
            {
                // Arrange
                string solutionName = Path.GetFileNameWithoutExtension(solutionPath);
                string computed = JbSolutionCacheHash.Compute(solutionPath);

                // Act
                string directoryName = JbSolutionCacheHash.FirstGenerationDirectoryName(solutionPath);
                string? parsed = JbCacheGenerations.MatchHash(directoryName, solutionName);

                // Assert — null here would mean the writer produced a name its own reader rejects, which is
                // how a solution silently stops owning its cache: nothing matches, so nothing is ever reset
                // or seeded.
                parsed.ShouldBe(
                    computed,
                    $"\"{directoryName}\" is what this server composes for \"{solutionPath}\", so parsing it "
                    + $"against \"{solutionName}\" must return that path's own hash \"{computed}\".");
            });
    }

    [Property]
    public Property Compute_TheSamePathInAnyAsciiCase_HashesAlike()
    {
        return Prop.ForAll(
            CaseVariant().ToArbitrary(),
            testCase =>
            {
                // Act
                string variantHash = JbSolutionCacheHash.Compute(testCase.Variant);

                // Assert — jb folds A–Z when it hashes a solution path, so a path typed in another case has
                // to land on the generation the first spelling built.
                variantHash.ShouldBe(
                    JbSolutionCacheHash.Compute(testCase.Path),
                    $"\"{testCase.Variant}\" is \"{testCase.Path}\" respelt in another ASCII case, so both "
                    + "must hash to the one generation directory.");
            });
    }

    [Property]
    public Property Compute_TwoPathsDifferingInOneCharacter_HashAlikeOnlyWhenItDiffersInAsciiCase()
    {
        return Prop.ForAll(
            Substitution().ToArbitrary(),
            testCase =>
            {
                // Arrange
                string hash = JbSolutionCacheHash.Compute(testCase.Path);

                // Act
                string mutatedHash = JbSolutionCacheHash.Compute(testCase.Mutated);

                // Assert
                if (FoldAlike(testCase.Original, testCase.Replacement))
                    mutatedHash.ShouldBe(
                        hash,
                        $"'{testCase.Original}' and '{testCase.Replacement}' are one ASCII letter in two "
                        + $"cases, so \"{testCase.Path}\" and \"{testCase.Mutated}\" name one solution.");
                else
                    mutatedHash.ShouldNotBe(
                        hash,
                        $"'{testCase.Original}' and '{testCase.Replacement}' do not fold together, so "
                        + $"\"{testCase.Path}\" and \"{testCase.Mutated}\" are two solutions — and a hash "
                        + "conflating them points a reset or a transplant at the wrong cache.");
            });
    }

    /// <summary>
    ///     Whether the fold under test maps both characters onto one: ASCII letters that agree once
    ///     lower-cased, and nothing else. Written from the rule rather than by calling the code under test,
    ///     so a fold that widens is a failure rather than a tautology.
    /// </summary>
    private static bool FoldAlike(char left, char right)
    {
        return char.IsAsciiLetter(left)
               && char.IsAsciiLetter(right)
               && char.ToLowerInvariant(left) == char.ToLowerInvariant(right);
    }

    /// <summary>
    ///     A solution path: the POSIX shape the shared generators produce, that same shape respelt with a
    ///     drive letter and backslashes, and the curated paths. A Windows spelling is legal input here
    ///     because <see cref="JbSolutionCacheHash.Compute" /> is a pure string function calling no path API,
    ///     so no property built on one can assert something true on a single platform.
    /// </summary>
    private static Gen<string> HashInputPath()
    {
        Gen<string> windowsShaped = JbNameGenerators.SolutionPath()
            .Select(path => "C:" + path.Replace('/', '\\'));

        return Gen.OneOf(JbNameGenerators.SolutionPath(), windowsShaped, Gen.Elements(CuratedPaths));
    }

    private static Gen<CaseVariantCase> CaseVariant()
    {
        return HashInputPath().SelectMany(
            JbNameGenerators.AsciiCaseVariant,
            (path, variant) => new CaseVariantCase(path, variant));
    }

    /// <summary>
    ///     A path, and the same path with exactly one character replaced by a different one. The replacement
    ///     is drawn from the trap characters as often as at random, so the pairs a wider fold would conflate
    ///     — the <c>| 0x20</c> neighbours of the bracket and separator characters, and the cased letters
    ///     outside ASCII — are hit on every seed.
    /// </summary>
    private static Gen<SubstitutionCase> Substitution()
    {
        Gen<char> replacement = Gen.OneOf(Gen.Elements(TrapCharacters), ArbMap.Default.GeneratorFor<char>());

        return HashInputPath()
            .SelectMany(path => Gen.Choose(0, path.Length - 1), (path, index) => (Path: path, Index: index))
            .SelectMany(_ => replacement, (drawn, character) => (drawn.Path, drawn.Index, Character: character))
            .Where(drawn => drawn.Character != drawn.Path[drawn.Index])
            .Select(drawn => new SubstitutionCase(drawn.Path, drawn.Index, drawn.Character));
    }

    /// <summary>A path and another ASCII-case spelling of it.</summary>
    private sealed record CaseVariantCase(string Path, string Variant);

    /// <summary>A path and the one-character substitution made in it.</summary>
    private sealed record SubstitutionCase(string Path, int Index, char Replacement)
    {
        /// <summary>The character the substitution replaced.</summary>
        internal char Original => Path[Index];

        /// <summary>The path the substitution produced.</summary>
        internal string Mutated => Path[..Index] + Replacement + Path[(Index + 1)..];
    }
}