using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Shouldly;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     States generation ownership over generated names rather than the handful a table can hold: a generation
///     directory belongs to the solution whose name composed it, and to no other.
/// </summary>
/// <remarks>
///     <see cref="MatchHash_GenerationOfALongerSolutionSharingThisPrefix_ReturnsNull" /> carries the invariant
///     that keeps <c>resharper_reset_cache</c> from deleting <c>_App.Core.*</c> when asked about <c>App</c>, which
///     a table of examples demonstrates only for the pairs someone wrote down.
/// </remarks>
public sealed class JbCacheGenerationsPropertyTests
{
    /// <summary>Draws a generation directory composed from the solution name it is then parsed against.</summary>
    private static Gen<OwnGenerationCase> OwnGeneration()
    {
        return JbNameGenerators.SolutionName().SelectMany(
            JbNameGenerators.GenerationDirectoryName,
            (solutionName, generation) =>
                new OwnGenerationCase(generation.DirectoryName, solutionName, generation.Hash));
    }

    /// <summary>
    ///     Draws a directory name that shares a shorter solution's prefix but is a longer solution's generation.
    /// </summary>
    /// <remarks>
    ///     Both ways of being longer are drawn: a further dot-separated segment (the <c>App</c> /
    ///     <c>App.Core</c> pair that occurs in real cache homes) and more characters on the last segment
    ///     (<c>App</c> / <c>AppCore</c>), which fails the prefix test rather than the remainder parse.
    /// </remarks>
    private static Gen<LongerSolutionCase> LongerSolutionGeneration()
    {
        Gen<(string Shorter, string Separator)> prefix = JbNameGenerators.SolutionName()
            .SelectMany(
                _ => Gen.Elements(".", ""),
                (shorterName, separator) => (Shorter: shorterName, Separator: separator));

        Gen<(string Shorter, string Longer)> names = prefix.SelectMany(
            _ => JbNameGenerators.SolutionName(),
            (parts, suffix) => (parts.Shorter, Longer: parts.Shorter + parts.Separator + suffix));

        return names.SelectMany(
            pair => JbNameGenerators.GenerationDirectoryName(pair.Longer),
            (pair, generation) =>
                new LongerSolutionCase(generation.DirectoryName, pair.Shorter, pair.Longer));
    }

    /// <summary>
    ///     Draws a generation directory built from another ASCII-case spelling of the solution name it is then
    ///     parsed against.
    /// </summary>
    private static Gen<CaseVariantCase> CaseVariantGeneration()
    {
        Gen<(string Name, string Variant)> spellings = JbNameGenerators.SolutionName()
            .SelectMany(JbNameGenerators.AsciiCaseVariant, (name, variant) => (Name: name, Variant: variant));

        return spellings.SelectMany(
            pair => JbNameGenerators.GenerationDirectoryName(pair.Variant),
            (pair, generation) =>
                new CaseVariantCase(generation.DirectoryName, pair.Name, pair.Variant, generation.Hash));
    }

    [Property]
    public Property MatchHash_ThisSolutionsOwnGeneration_ReadsBackTheHashVerbatim()
    {
        return Prop.ForAll(
            OwnGeneration().ToArbitrary(),
            testCase =>
            {
                // Act
                string? hash = JbCacheGenerations.MatchHash(testCase.DirectoryName, testCase.SolutionName);

                // Assert — verbatim, because the hash is compared against a computed one as a string. A parse
                // that normalised it (dropping a leading zero, say) would read back a value matching nothing.
                hash.ShouldBe(
                    testCase.Hash,
                    $"\"{testCase.DirectoryName}\" was composed from solution \"{testCase.SolutionName}\" and "
                    + $"hash \"{testCase.Hash}\", so parsing it must return that hash unchanged.");
            });
    }

    [Property]
    public Property MatchHash_GenerationOfALongerSolutionSharingThisPrefix_ReturnsNull()
    {
        return Prop.ForAll(
            LongerSolutionGeneration().ToArbitrary(),
            testCase =>
            {
                // Act
                string? hash = JbCacheGenerations.MatchHash(testCase.DirectoryName, testCase.ShorterName);

                // Assert
                hash.ShouldBeNull(
                    $"\"{testCase.DirectoryName}\" is a generation of solution \"{testCase.LongerName}\", so it "
                    + $"is not solution \"{testCase.ShorterName}\"'s to report — and a caller that deletes what "
                    + "this returns would drop another solution's cache.");
            });
    }

    /// <summary>
    ///     Stated through <see cref="JbCacheGenerations.NameComparison" /> rather than behind a platform skip,
    ///     so it means the same thing on every operating system: on Windows every draw exercises it, and
    ///     elsewhere the identity spellings among the draws do.
    /// </summary>
    /// <remarks>
    ///     The converse is left unpinned. Off Windows the comparison is ordinal, a proxy for the cache home's
    ///     filesystem that is wrong on a case-insensitive one, and a test asserting it would freeze the proxy in
    ///     place.
    /// </remarks>
    [Property]
    public Property MatchHash_ASolutionNameSpeltInAnotherCase_MatchesWhereverNameComparisonCallsItTheSameName()
    {
        return Prop.ForAll(
            CaseVariantGeneration().ToArbitrary(),
            testCase =>
            {
                if (!string.Equals(testCase.Variant, testCase.SolutionName, JbCacheGenerations.NameComparison))
                    return;

                // Act
                string? hash = JbCacheGenerations.MatchHash(testCase.DirectoryName, testCase.SolutionName);

                // Assert
                hash.ShouldBe(
                    testCase.Hash,
                    $"\"{testCase.DirectoryName}\" was composed from \"{testCase.Variant}\", which compares "
                    + $"equal to \"{testCase.SolutionName}\", so that solution owns it — and a parser "
                    + "declining it leaves the solution owning none of its own cache.");
            });
    }

    /// <summary>Holds a generation directory, the solution name it was composed from, and its hash.</summary>
    private sealed record OwnGenerationCase(string DirectoryName, string SolutionName, string Hash);

    /// <summary>Holds a longer solution's generation directory, and the shorter name it must not answer to.</summary>
    private sealed record LongerSolutionCase(string DirectoryName, string ShorterName, string LongerName);

    /// <summary>
    ///     Holds a generation directory, the solution name it is parsed against, the spelling of that name it was
    ///     composed from, and the hash it carries.
    /// </summary>
    private sealed record CaseVariantCase(
        string DirectoryName,
        string SolutionName,
        string Variant,
        string Hash);
}