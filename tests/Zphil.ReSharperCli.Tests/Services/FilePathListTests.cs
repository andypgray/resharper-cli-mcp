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
///     <see cref="FilePathList.ToIncludePattern" /> is the other half: the spelling <c>jb</c>'s
///     <c>--include</c> can actually match, which is relative-only.
/// </summary>
public sealed class FilePathListTests : IDisposable
{
    private readonly FakeEnvironment _environment = new();
    private readonly string _solutionDirectory;

    public FilePathListTests()
    {
        _solutionDirectory = _environment.CurrentDirectory;
    }

    public static bool OnWindows => OperatingSystem.IsWindows();

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public void Split_CommaJoinedEntry_YieldsOnePathPerFragment()
    {
        // Act
        IReadOnlyList<string> split = FilePathList.Split(["src/A.cs, src/B.cs"], _solutionDirectory);

        // Assert
        split.ShouldBe(["src/A.cs", "src/B.cs"]);
    }

    [Fact]
    public void Split_SemicolonJoinedEntry_YieldsOnePathPerFragment()
    {
        // Arrange — a semicolon already reaches jb as its own separator (both tools join files with ";"), so
        // inspect happens to work today and cleanup rejects it. Splitting makes the two agree.

        // Act
        IReadOnlyList<string> split = FilePathList.Split(["src/A.cs;src/B.cs"], _solutionDirectory);

        // Assert
        split.ShouldBe(["src/A.cs", "src/B.cs"]);
    }

    [Fact]
    public void Split_EntryMixingBothDelimiters_SplitsOnEach()
    {
        // Act
        IReadOnlyList<string> split = FilePathList.Split(["a.cs;b.cs,c.cs"], _solutionDirectory);

        // Assert
        split.ShouldBe(["a.cs", "b.cs", "c.cs"]);
    }

    [Fact]
    public void Split_FragmentsPaddedWithWhitespace_AreTrimmed()
    {
        // Act
        IReadOnlyList<string> split = FilePathList.Split(["  src/A.cs  ;  src/B.cs  "], _solutionDirectory);

        // Assert
        split.ShouldBe(["src/A.cs", "src/B.cs"]);
    }

    [Fact]
    public void Split_EmptyFragments_AreDropped()
    {
        // Arrange — a trailing delimiter or a doubled one is exactly what hand-joining produces, and an empty
        // fragment would reach jb as an --include pattern matching nothing.

        // Act
        IReadOnlyList<string> split = FilePathList.Split(["a.cs,,b.cs,"], _solutionDirectory);

        // Assert
        split.ShouldBe(["a.cs", "b.cs"]);
    }

    [Fact]
    public void Split_JoinedGlobs_KeepsEachPatternIntact()
    {
        // Arrange — inspect's argument is globs, not paths; splitting must not disturb the wildcards.

        // Act
        IReadOnlyList<string> split = FilePathList.Split(["src/**/*.cs;tests/**/*.cs"], _solutionDirectory);

        // Assert
        split.ShouldBe(["src/**/*.cs", "tests/**/*.cs"]);
    }

    [Fact]
    public void Split_EntryWithNoDelimiter_ReturnsTheOriginalList()
    {
        // Arrange
        string[] files = ["src/A.cs", "src/**/*.cs"];

        // Act
        IReadOnlyList<string> split = FilePathList.Split(files, _solutionDirectory);

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
        IReadOnlyList<string> split = FilePathList.Split(files, _solutionDirectory);

        // Assert
        split.ShouldBeSameAs(files);
    }

    [Fact]
    public void Split_MixedList_SplitsOnlyTheJoinedEntries()
    {
        // Act
        IReadOnlyList<string> split = FilePathList.Split(
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
        IReadOnlyList<string> split = FilePathList.Split(
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
        IReadOnlyList<string> split = FilePathList.Split(files, _solutionDirectory);

        // Assert
        split.ShouldBeSameAs(files);
    }

    [Fact]
    public void Split_NullFiles_PassesThrough()
    {
        // Act — inspect's files argument is optional, and a solution-wide scan must stay solution-wide.
        IReadOnlyList<string>? split = FilePathList.Split(null, _solutionDirectory);

        // Assert
        split.ShouldBeNull();
    }

    [Fact]
    public void Split_EmptyFiles_ReturnsTheOriginalList()
    {
        // Arrange
        string[] files = [];

        // Act
        IReadOnlyList<string> split = FilePathList.Split(files, _solutionDirectory);

        // Assert
        split.ShouldBeSameAs(files);
    }

    [Fact]
    public void ToIncludePattern_AbsolutePathUnderTheSolution_BecomesAForwardSlashedRelativePath()
    {
        // Arrange — jb's --include takes "a set of relative paths" and matches them against the solution
        // model, so an absolute entry is an Ant pattern that matches nothing at all.
        string absolute = Path.Combine(_solutionDirectory, "src", "A.cs");

        // Act
        string pattern = FilePathList.ToIncludePattern(absolute, _solutionDirectory);

        // Assert
        pattern.ShouldBe("src/A.cs");
    }

    [Fact(Skip = "Only Windows spells a path with a drive letter.", SkipUnless = nameof(OnWindows))]
    public void ToIncludePattern_TheFieldSpelling_Resolves()
    {
        // Arrange — verbatim from the field report: a lowercase drive letter and forward slashes, which is
        // how an agent tends to write a Windows path. GetRelativePath compares case-insensitively on Windows,
        // so the drive letter is not the problem the report guessed it was — being absolute at all is.
        const string solutionDirectory = @"C:\Users\dev\source\repos\app";
        const string entry = "c:/Users/dev/source/repos/app/tests/App.Tests/Foo/BarTests.cs";

        // Act
        string pattern = FilePathList.ToIncludePattern(entry, solutionDirectory);

        // Assert
        pattern.ShouldBe("tests/App.Tests/Foo/BarTests.cs");
    }

    [Fact(Skip = "Only Windows has a volume a path can be relative to.", SkipUnless = nameof(OnWindows))]
    public void ToIncludePattern_PathOnAnotherVolume_StaysAbsolute()
    {
        // Arrange — two volumes have no relative path between them, so GetRelativePath hands the target back
        // unchanged. Nothing better exists: an --include that cannot be relativised cannot be made to match.

        // Act
        string pattern = FilePathList.ToIncludePattern(@"D:\other\src\A.cs", @"C:\repo");

        // Assert
        pattern.ShouldBe("D:/other/src/A.cs");
    }

    [Fact(Skip = "Only Windows has drive-relative paths.", SkipUnless = nameof(OnWindows))]
    public void ToIncludePattern_DriveRelativePath_IsUntouched()
    {
        // Arrange — the reason the test is IsPathFullyQualified rather than IsPathRooted. On Windows
        // "/src/Foo.cs" is rooted but drive-relative; it is already the relative form jb wants, and
        // relativising it against the solution directory would turn it into "../src/Foo.cs".

        // Act
        string pattern = FilePathList.ToIncludePattern("/src/Foo.cs", _solutionDirectory);

        // Assert
        pattern.ShouldBe("/src/Foo.cs");
    }

    [Fact]
    public void ToIncludePattern_AlreadyRelative_IsUntouched()
    {
        // Act — the spelling jb documents, arriving as documented.
        string pattern = FilePathList.ToIncludePattern("src/A.cs", _solutionDirectory);

        // Assert
        pattern.ShouldBe("src/A.cs");
    }

    [Fact]
    public void ToIncludePattern_RootedWildcard_KeepsItsWildcards()
    {
        // Arrange — inspect's argument is globs, and an absolute one is just as unmatchable as an absolute
        // path. Relativising must not disturb the wildcards it carries.
        string absolute = Path.Combine(_solutionDirectory, "src", "**", "*.cs");

        // Act
        string pattern = FilePathList.ToIncludePattern(absolute, _solutionDirectory);

        // Assert
        pattern.ShouldBe("src/**/*.cs");
    }

    [Fact]
    public void ToIncludePattern_PathOutsideTheSolutionDirectory_BecomesTheParentRelativeForm()
    {
        // Arrange — a project living above the solution file is a legitimate layout, so this is translated
        // best-effort rather than rejected: "../" is still the relative path jb asked for.
        string outside = Path.Combine(Path.GetDirectoryName(_solutionDirectory)!, "shared", "A.cs");

        // Act
        string pattern = FilePathList.ToIncludePattern(outside, _solutionDirectory);

        // Assert
        pattern.ShouldBe("../shared/A.cs");
    }

    [Fact]
    public void ToIncludePattern_PathTheRuntimeRejects_IsKeptVerbatimRatherThanThrowing()
    {
        // Arrange — an embedded null throws out of the path APIs. Translation runs on the way to jb, so it
        // must leave a malformed entry for the validation that reports it rather than adding a crash.
        string malformed = _solutionDirectory + Path.DirectorySeparatorChar + "\0.cs";

        // Act
        string pattern = FilePathList.ToIncludePattern(malformed, _solutionDirectory);

        // Assert
        pattern.ShouldBe(malformed);
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