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
public sealed class JbSolutionCacheHashPropertyTests
{
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
}