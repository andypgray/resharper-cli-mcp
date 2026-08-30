using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Shouldly;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     The safety-critical predicate of the cache tools, stated over generated names rather than the handful
///     a table can hold: a generation directory belongs to the solution whose name composed it, and to no
///     other. The second property is the one that matters — it is the exact invariant that stops
///     <c>resharper_reset_cache</c> deleting <c>_App.Core.*</c> when it was asked about <c>App</c>, and a
///     table of examples can only ever demonstrate it for the pairs someone thought to write down.
/// </summary>
public sealed class JbCacheGenerationsPropertyTests
{
    /// <summary>A generation directory composed from the very solution name it is then parsed against.</summary>
    private static Gen<OwnGenerationCase> OwnGeneration()
    {
        return JbNameGenerators.SolutionName().SelectMany(
            JbNameGenerators.GenerationDirectoryName,
            (solutionName, generation) =>
                new OwnGenerationCase(generation.DirectoryName, solutionName, generation.Hash));
    }

    /// <summary>
    ///     A directory name that shares a shorter solution's prefix but is a longer solution's generation.
    ///     Both ways of being longer are drawn: a further dot-separated segment (the <c>App</c> /
    ///     <c>App.Core</c> pair that occurs in real cache homes) and more characters on the last segment
    ///     (<c>App</c> / <c>AppCore</c>), which fails the prefix test rather than the remainder parse.
    /// </summary>
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

    /// <summary>A generation directory, the solution name it was composed from, and the hash it carries.</summary>
    private sealed record OwnGenerationCase(string DirectoryName, string SolutionName, string Hash);

    /// <summary>A longer solution's generation directory, and the shorter name it must not answer to.</summary>
    private sealed record LongerSolutionCase(string DirectoryName, string ShorterName, string LongerName);
}