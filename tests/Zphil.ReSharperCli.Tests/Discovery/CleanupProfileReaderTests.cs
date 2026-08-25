using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Discovery;

/// <summary>
///     The settings-file reader on its own: which files it can get a declared profile out of, and what it
///     reports when it cannot. The load-bearing case is the one it was written for — a file <c>jb</c> reads
///     without complaint but the XML spec rejects must not turn the declared-profile feature off.
/// </summary>
public sealed class CleanupProfileReaderTests : IDisposable
{
    private readonly FakeEnvironment _environment = new();

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public void Read_WellFormedSettingsDeclaringAProfile_ReturnsTheName()
    {
        // Arrange
        string path = Write(DotSettingsFixtures.Declaring("House: Keep Named Arguments"));

        // Act
        DeclaredCleanupProfile declared = CleanupProfileReader.Read(path, NullLogger.Instance);

        // Assert
        declared.Name.ShouldBe("House: Keep Named Arguments");
        declared.Failure.ShouldBeNull();
    }

    [Fact]
    public void Read_IllegalDoubleHyphenInsideAComment_StillReturnsTheDeclaredName()
    {
        // Arrange — the regression this whole change exists for. `--` inside a comment is illegal XML, .NET
        // has no lenient mode for it (XmlReaderSettings.CheckCharacters = false does not relax the rule), and
        // ReSharper reads such a file happily. Rejecting it here silently applied Full Cleanup instead of the
        // profile the repo declared — the exact rewrite that profile was defined to prevent.
        string path = Write(DotSettingsFixtures.DeclaringBehindIllegalComment("House: Keep Named Arguments"));

        // Act
        DeclaredCleanupProfile declared = CleanupProfileReader.Read(path, NullLogger.Instance);

        // Assert
        declared.Name.ShouldBe("House: Keep Named Arguments");
        declared.Failure.ShouldBeNull(); // recovered, so there is nothing to warn the caller about
    }

    [Fact]
    public void Read_CommentBodyContainingAnEarlyCommentClose_StopsAtTheFirstOne()
    {
        // Arrange — the lenient pass discards `<!-- ... -->` non-greedily, which is XML's own rule: the first
        // `-->` ends the comment and everything after it is markup again. A greedy match would swallow the
        // declaration and read the file as declaring nothing.
        string path = Write(
            """
            <wpf:ResourceDictionary xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:s="clr-namespace:System;assembly=mscorlib" xmlns:wpf="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            	<!-- illegal -- here -->
            	<s:String x:Key="/Default/CodeStyle/CodeCleanup/SilentCleanupProfile/@EntryValue">House: Keep Named Arguments</s:String>
            	<!-- and another -- one -->
            </wpf:ResourceDictionary>
            """);

        // Act
        DeclaredCleanupProfile declared = CleanupProfileReader.Read(path, NullLogger.Instance);

        // Assert
        declared.Name.ShouldBe("House: Keep Named Arguments");
    }

    [Fact]
    public void Read_BrokenBeyondCommentStripping_ReportsTheFailureWithPathAndReason()
    {
        // Arrange
        string path = Write(DotSettingsFixtures.Unparseable());

        // Act
        DeclaredCleanupProfile declared = CleanupProfileReader.Read(path, NullLogger.Instance);

        // Assert — no exception escapes, and the caller gets enough to act on rather than a bare null.
        declared.Name.ShouldBeNull();
        declared.Failure.ShouldNotBeNull();
        declared.Failure.Path.ShouldBe(path);
        declared.Failure.Reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Read_BrokenBeyondCommentStripping_ReasonNamesTheRealFaultAtItsRealLineNumber()
    {
        // Arrange — the reported reason comes from the lenient pass, so it names what is genuinely still
        // wrong rather than the comment we deliberately tolerated. Comments are replaced by the newlines they
        // spanned, which is what keeps that line number pointing at the real file.
        string path = Write(DotSettingsFixtures.Unparseable());

        // Act
        DeclaredCleanupProfile declared = CleanupProfileReader.Read(path, NullLogger.Instance);

        // Assert
        declared.Failure.ShouldNotBeNull();
        declared.Failure.Reason.ShouldNotContain("comment"); // not the fault we tolerated
        declared.Failure.Reason.ShouldContain("s:String");
        declared.Failure.Reason.ShouldContain("line 3"); // the unclosed element's line in the original file
    }

    [Fact]
    public void Read_SettingsWithoutTheEntry_ReturnsNeitherNameNorFailure()
    {
        // Arrange — a settings file that tunes something else entirely. "Declares nothing" is not a failure,
        // and must not produce a warning: it is the ordinary case for most repos.
        string path = Write(
            """
            <wpf:ResourceDictionary xml:space="preserve" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:s="clr-namespace:System;assembly=mscorlib" xmlns:wpf="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            	<s:String x:Key="/Default/CodeInspection/Highlighting/InspectionSeverities/=RedundantCast/@EntryIndexedValue">DO_NOT_SHOW</s:String>
            </wpf:ResourceDictionary>
            """);

        // Act
        DeclaredCleanupProfile declared = CleanupProfileReader.Read(path, NullLogger.Instance);

        // Assert
        declared.Name.ShouldBeNull();
        declared.Failure.ShouldBeNull();
    }

    [Fact]
    public void Read_BlankDeclaredProfile_ReadsAsUnsetRatherThanAsAFailure()
    {
        // Arrange — a blank name would reach jb as --profile= and fail the run.
        string path = Write(DotSettingsFixtures.Declaring("   "));

        // Act
        DeclaredCleanupProfile declared = CleanupProfileReader.Read(path, NullLogger.Instance);

        // Assert
        declared.Name.ShouldBeNull();
        declared.Failure.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Read_NoSettingsPath_ReturnsNeitherNameNorFailure(string? path)
    {
        // Act
        DeclaredCleanupProfile declared = CleanupProfileReader.Read(path, NullLogger.Instance);

        // Assert — no settings file is the default state, not a misconfiguration.
        declared.Name.ShouldBeNull();
        declared.Failure.ShouldBeNull();
    }

    [Fact]
    public void Read_PathThatDoesNotExist_ReportsTheFailureWithoutThrowing()
    {
        // Arrange — the resolver only hands over paths it has seen exist, but a file can be deleted between
        // the two, and this method's contract is that it never throws.
        string path = Path.Combine(_environment.CurrentDirectory, "gone.DotSettings");

        // Act
        DeclaredCleanupProfile declared = CleanupProfileReader.Read(path, NullLogger.Instance);

        // Assert
        declared.Name.ShouldBeNull();
        declared.Failure.ShouldNotBeNull();
        declared.Failure.Path.ShouldBe(path);
    }

    private string Write(string content)
    {
        string path = Path.Combine(_environment.CurrentDirectory, "App.sln.DotSettings");
        File.WriteAllText(path, content);
        return path;
    }
}