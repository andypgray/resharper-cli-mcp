using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;

namespace Zphil.ReSharperCli.Tests.Services;

/// <summary>
///     <see cref="FilePathList.Split" /> rescues the measured caller mistake of joining several paths into
///     one <c>files</c> element, without ever reinterpreting an element that names a real file. These tests
///     plant real files under a per-instance temp directory (so the parallel run stays race-free), because
///     the existing-file guard is the whole reason splitting is safe for the destructive tool.
/// </summary>
public sealed class FilePathListTests : IDisposable
{
    private readonly FakeEnvironment _environment = new();
    private readonly string _solutionDirectory;

    public FilePathListTests()
    {
        _solutionDirectory = _environment.CurrentDirectory;
    }

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public void Split_CommaJoinedEntry_YieldsOnePathPerFragment()
    {
        // Act
        var split = FilePathList.Split(["src/A.cs, src/B.cs"], _solutionDirectory);

        // Assert
        split.ShouldBe(["src/A.cs", "src/B.cs"]);
    }

    [Fact]
    public void Split_SemicolonJoinedEntry_YieldsOnePathPerFragment()
    {
        // Arrange — a semicolon already reaches jb as its own separator (both tools join files with ";"), so
        // inspect happens to work today and cleanup rejects it. Splitting makes the two agree.

        // Act
        var split = FilePathList.Split(["src/A.cs;src/B.cs"], _solutionDirectory);

        // Assert
        split.ShouldBe(["src/A.cs", "src/B.cs"]);
    }

    [Fact]
    public void Split_EntryMixingBothDelimiters_SplitsOnEach()
    {
        // Act
        var split = FilePathList.Split(["a.cs;b.cs,c.cs"], _solutionDirectory);

        // Assert
        split.ShouldBe(["a.cs", "b.cs", "c.cs"]);
    }

    [Fact]
    public void Split_FragmentsPaddedWithWhitespace_AreTrimmed()
    {
        // Act
        var split = FilePathList.Split(["  src/A.cs  ;  src/B.cs  "], _solutionDirectory);

        // Assert
        split.ShouldBe(["src/A.cs", "src/B.cs"]);
    }

    [Fact]
    public void Split_EmptyFragments_AreDropped()
    {
        // Arrange — a trailing delimiter or a doubled one is exactly what hand-joining produces, and an empty
        // fragment would reach jb as an --include pattern matching nothing.

        // Act
        var split = FilePathList.Split(["a.cs,,b.cs,"], _solutionDirectory);

        // Assert
        split.ShouldBe(["a.cs", "b.cs"]);
    }

    [Fact]
    public void Split_JoinedGlobs_KeepsEachPatternIntact()
    {
        // Arrange — inspect's argument is globs, not paths; splitting must not disturb the wildcards.

        // Act
        var split = FilePathList.Split(["src/**/*.cs;tests/**/*.cs"], _solutionDirectory);

        // Assert
        split.ShouldBe(["src/**/*.cs", "tests/**/*.cs"]);
    }

    [Fact]
    public void Split_EntryWithNoDelimiter_ReturnsTheOriginalList()
    {
        // Arrange
        string[] files = ["src/A.cs", "src/**/*.cs"];

        // Act
        var split = FilePathList.Split(files, _solutionDirectory);

        // Assert — the common path allocates nothing.
        split.ShouldBeSameAs(files);
    }

    [Fact]
    public void Split_EntryNamingAFileThatExists_IsNeverSplit()
    {
        // Arrange — the guard that makes splitting safe for the destructive tool: a comma is a legal filename
        // character, so an entry that already resolves to a real file is kept verbatim.
        string[] files = ["Foo,Bar.cs"];
        PlantFile("Foo,Bar.cs");

        // Act
        var split = FilePathList.Split(files, _solutionDirectory);

        // Assert
        split.ShouldBeSameAs(files);
    }

    [Fact]
    public void Split_MixedList_SplitsOnlyTheJoinedEntries()
    {
        // Act
        var split = FilePathList.Split(
            ["src/A.cs", "src/B.cs;src/C.cs", "src/D.cs"], _solutionDirectory);

        // Assert — request order is preserved, with the fragments in the joined entry's place.
        split.ShouldBe(["src/A.cs", "src/B.cs", "src/C.cs", "src/D.cs"]);
    }

    [Fact]
    public void Split_SeveralJoinedEntries_AreEachSplitInPlace()
    {
        // Arrange — a caller who joins once tends to join throughout, so the second joined entry must be
        // split into the list the first one started rather than replacing it.

        // Act
        var split = FilePathList.Split(
            ["a.cs;b.cs", "c.cs", "d.cs,e.cs"], _solutionDirectory);

        // Assert
        split.ShouldBe(["a.cs", "b.cs", "c.cs", "d.cs", "e.cs"]);
    }

    [Fact]
    public void Split_EntryOfNothingButDelimiters_IsKeptVerbatim()
    {
        // Arrange — splitting this yields no fragments at all. Keeping it lets the caller's own list reach the
        // existing validation, which names what was actually sent rather than silently dropping the entry.
        string[] files = [" , ; "];

        // Act
        var split = FilePathList.Split(files, _solutionDirectory);

        // Assert
        split.ShouldBeSameAs(files);
    }

    [Fact]
    public void Split_NullFiles_PassesThrough()
    {
        // Act — inspect's files argument is optional, and a solution-wide scan must stay solution-wide.
        var split = FilePathList.Split(null, _solutionDirectory);

        // Assert
        split.ShouldBeNull();
    }

    [Fact]
    public void Split_EmptyFiles_ReturnsTheOriginalList()
    {
        // Arrange
        string[] files = [];

        // Act
        var split = FilePathList.Split(files, _solutionDirectory);

        // Assert
        split.ShouldBeSameAs(files);
    }

    [Fact]
    public void ResolvesToExistingFile_AbsolutePath_IgnoresTheSolutionDirectory()
    {
        // Arrange
        string absolute = PlantFile("src/A.cs");

        // Act & Assert
        FilePathList.ResolvesToExistingFile(absolute, _solutionDirectory).ShouldBeTrue();
        FilePathList.ResolvesToExistingFile("src/A.cs", _solutionDirectory).ShouldBeTrue();
        FilePathList.ResolvesToExistingFile("src/Missing.cs", _solutionDirectory).ShouldBeFalse();
    }

    [Fact]
    public void ResolvesToExistingFile_PathTheRuntimeRejects_IsFalseRatherThanThrowing()
    {
        // Arrange — an embedded null throws out of Path.GetFullPath. It names no file, and this predicate
        // runs before validation has had a chance to reject anything.

        // Act & Assert
        FilePathList.ResolvesToExistingFile("src/\0.cs", _solutionDirectory).ShouldBeFalse();
    }

    private string PlantFile(string relativePath)
    {
        string fullPath = Path.Combine(_solutionDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, string.Empty);
        return fullPath;
    }
}