using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Services;

/// <summary>
///     Pins <see cref="FilePathList" /> over real files planted under a per-instance temp directory (so the
///     parallel run stays race-free), because the existing-file guard is the whole reason splitting is safe
///     for the destructive tool.
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

    /// <summary>
    ///     Lists in which no entry needs splitting.
    /// </summary>
    /// <remarks>
    ///     The common path allocates nothing, so the caller's own list comes back rather than a copy of it.
    /// </remarks>
    public static TheoryData<string[]> NothingToSplit => new()
    {
        new[] { "src/A.cs", "src/**/*.cs" },
        Array.Empty<string>()
    };

    /// <summary>
    ///     Entries the path APIs refuse outright, one per way they refuse.
    /// </summary>
    /// <remarks>
    ///     Relative, so the row needs no instance state: <see cref="FilePathList.ResolvesToExistingFile" />
    ///     resolves it against the solution directory, which is where the refusal happens.
    /// </remarks>
    public static TheoryData<string> EntriesTheRuntimeRefuses => new()
    {
        // An embedded null, which Path.GetFullPath throws on as an ArgumentException.
        "src/\0.cs",
        // An entry past the NT path limit rather than the legacy 260-character one, which the runtime no longer
        // enforces. Windows raises PathTooLongException, an IOException and not an ArgumentException, so it
        // reaches the other catch.
        new string('a', 40_000)
    };

    public void Dispose()
    {
        _environment.Dispose();
    }

    // Each row is one facet of the rule: split on "," and ";", trim each fragment, drop the empty ones, and keep
    // request order with the fragments in the joined entry's place. A Split counterexample found by
    // FilePathListPropertyTests lands here as a row.
    [Theory]
    [InlineData(new[] { "src/A.cs, src/B.cs" }, new[] { "src/A.cs", "src/B.cs" })]
    // A semicolon already reaches jb as its own separator (both tools join files with ";"), so without the split
    // inspect works and cleanup rejects it. Splitting makes the two agree.
    [InlineData(new[] { "src/A.cs;src/B.cs" }, new[] { "src/A.cs", "src/B.cs" })]
    [InlineData(new[] { "a.cs;b.cs,c.cs" }, new[] { "a.cs", "b.cs", "c.cs" })]
    [InlineData(new[] { "  src/A.cs  ;  src/B.cs  " }, new[] { "src/A.cs", "src/B.cs" })]
    // A trailing delimiter or a doubled one is exactly what hand-joining produces, and an empty fragment would
    // reach jb as an --include pattern matching nothing.
    [InlineData(new[] { "a.cs,,b.cs," }, new[] { "a.cs", "b.cs" })]
    // Inspect's argument is globs, not paths; splitting must not disturb the wildcards.
    [InlineData(new[] { "src/**/*.cs;tests/**/*.cs" }, new[] { "src/**/*.cs", "tests/**/*.cs" })]
    [InlineData(new[] { "src/A.cs", "src/B.cs;src/C.cs", "src/D.cs" }, new[] { "src/A.cs", "src/B.cs", "src/C.cs", "src/D.cs" })]
    // A caller who joins once tends to join throughout, so the second joined entry must be split into the list
    // the first one started rather than replacing it.
    [InlineData(new[] { "a.cs;b.cs", "c.cs", "d.cs,e.cs" }, new[] { "a.cs", "b.cs", "c.cs", "d.cs", "e.cs" })]
    public void Split_JoinedEntries_ExpandInPlaceOnEitherDelimiter(string[] files, string[] expected)
    {
        // Act
        IReadOnlyList<string> split = FilePathList.Split(files, _solutionDirectory);

        // Assert
        split.ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(NothingToSplit))]
    public void Split_NothingToSplit_ReturnsTheOriginalList(string[] files)
    {
        // Act
        IReadOnlyList<string> split = FilePathList.Split(files, _solutionDirectory);

        // Assert
        split.ShouldBeSameAs(files);
    }

    [Fact]
    public void Split_EntryNamingAFileThatExists_IsNeverSplit()
    {
        // Arrange — the guard that makes splitting safe for the destructive tool: a comma is a legal filename
        // character, so an entry that already resolves to a real file is kept verbatim.
        string[] files = ["Foo,Bar.cs"];
        SolutionFiles.Plant(_solutionDirectory, "Foo,Bar.cs");

        // Act
        IReadOnlyList<string> split = FilePathList.Split(files, _solutionDirectory);

        // Assert
        split.ShouldBeSameAs(files);
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

    // jb's --include takes "a set of relative paths" and matches them against the solution model, so an
    // absolute entry is an Ant pattern that matches nothing at all.
    [Theory]
    [InlineData("src/A.cs")]
    // Inspect's argument is globs, and an absolute one is just as unmatchable as an absolute path. Relativising
    // must not disturb the wildcards it carries.
    [InlineData("src/**/*.cs")]
    // A project living above the solution file is a legitimate layout, so this is translated best-effort rather
    // than rejected: "../" is still the relative path jb asked for.
    [InlineData("../shared/A.cs")]
    public void ToIncludePattern_FullyQualifiedEntry_IsRespeltRelativeToTheSolution(string relative)
    {
        // Arrange
        string fullyQualified = Path.GetFullPath(Path.Combine(_solutionDirectory, relative));

        // Act
        string pattern = FilePathList.ToIncludePattern(fullyQualified, _solutionDirectory);

        // Assert — forward-slashed whatever the platform's separator.
        pattern.ShouldBe(relative);
    }

    [Fact(Skip = "Only Windows spells a path with a drive letter.", SkipUnless = nameof(OnWindows))]
    public void ToIncludePattern_TheFieldSpelling_Resolves()
    {
        // Arrange — a real agent's spelling, verbatim: a lowercase drive letter and forward slashes, which is
        // how an agent tends to write a Windows path. GetRelativePath compares case-insensitively on Windows,
        // so the drive letter is not the problem it looks like — being absolute at all is.
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
    public void ToIncludePattern_EntryBeyondTheOsPathLimit_IsKeptRatherThanThrowing()
    {
        // Arrange — the second way an entry can be refused, and it arrives as a different exception:
        // Windows maps the length failure to PathTooLongException, which is an IOException and not an
        // ArgumentException.
        string tooLong = EntryBeyondTheOsPathLimit();

        // Act & Assert — the value is left unasserted on purpose: Windows hands it back verbatim, while
        // Unix's managed GetFullPath has no length to fail on and relativises it like any other path.
        Should.NotThrow(() => FilePathList.ToIncludePattern(tooLong, _solutionDirectory));
    }

    [Fact]
    public void ResolvesToExistingFile_AbsolutePath_IgnoresTheSolutionDirectory()
    {
        // Arrange
        string absolute = SolutionFiles.Plant(_solutionDirectory, "src/A.cs");

        // Act & Assert
        FilePathList.ResolvesToExistingFile(absolute, _solutionDirectory).ShouldBeTrue();
        FilePathList.ResolvesToExistingFile("src/A.cs", _solutionDirectory).ShouldBeTrue();
        FilePathList.ResolvesToExistingFile("src/Missing.cs", _solutionDirectory).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(EntriesTheRuntimeRefuses))]
    public void ResolvesToExistingFile_EntryTheRuntimeRefuses_IsFalseRatherThanThrowing(string refused)
    {
        // Act & Assert — a refused entry names no file, and this predicate runs before validation has had a
        // chance to reject anything. It decides whether an entry splits and whether the call is rejected as
        // missing, so a throw here fails both tools with an unexpected error instead of naming the entry.
        FilePathList.ResolvesToExistingFile(refused, _solutionDirectory).ShouldBeFalse();
    }

    [Fact]
    public void FindMissing_ReportsTheEntriesThatNameNoFile_AndLeavesTheOthersOut()
    {
        // Arrange
        SolutionFiles.Plant(_solutionDirectory, "src/A.cs");

        // Act
        List<string> missing = FilePathList.FindMissing(["src/A.cs", "src/Typo.cs"], _solutionDirectory);

        // Assert
        missing.ShouldBe(["src/Typo.cs"]);
    }

    [Fact]
    public void FindMissing_WildcardEntries_AreNeverReported()
    {
        // jb expands a pattern against the solution model, so this server cannot say what it matched — and
        // a pattern that resolves to no file on disk is the normal case, not a defect.
        FilePathList.FindMissing(["src/**/*.cs", "tests/**/*.cs"], _solutionDirectory).ShouldBeEmpty();
    }

    [Fact]
    public void FindMissing_ABlankEntry_IsReportedAsMissingRatherThanThrowing()
    {
        // Inspect has no blank guard and must not grow one: a read-only tool that throws on a malformed
        // list is worse than one that names the entry it could not use.
        FilePathList.FindMissing([""], _solutionDirectory).ShouldBe([""]);
    }

    /// <summary>
    ///     An entry long enough to pass the NT path limit rather than the legacy 260-character one, which
    ///     the runtime no longer enforces.
    /// </summary>
    private string EntryBeyondTheOsPathLimit()
    {
        return _solutionDirectory + Path.DirectorySeparatorChar + new string('a', 40_000);
    }
}