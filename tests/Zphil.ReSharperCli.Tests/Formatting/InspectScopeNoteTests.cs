using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Formatting;

namespace Zphil.ReSharperCli.Tests.Formatting;

/// <summary>
///     Pins both halves of <see cref="InspectScopeNote" />: it fires on entries the tool found to resolve to
///     nothing, and says nothing at all otherwise, since a note on every scoped call would be noise charged to
///     every response's budget.
/// </summary>
/// <remarks>
///     The classification is <c>FilePathList.FindMissing</c>'s and is pinned beside it, so these tests hand
///     the note its answer and touch no disk.
/// </remarks>
public sealed class InspectScopeNoteTests
{
    private const string Root = "/repo";

    [Fact]
    public void For_NothingMissing_SaysNothing()
    {
        // The overwhelmingly common calls: no files argument at all, or a scope in which every entry
        // resolved. Empty rather than blank, so the banner concatenation adds no separator either.
        InspectScopeNote.For([], 0, Root).ShouldBeEmpty();
        InspectScopeNote.For([], 2, Root).ShouldBeEmpty();
    }

    [Fact]
    public void For_AnEntryThatNamesNoFile_NamesItAndTheScopeItWasPartOf()
    {
        // The partial case, which no jb release reports: one entry matches, jb exits 0 with its findings, and
        // nothing anywhere mentions the other.
        string note = InspectScopeNote.For(["src/Typo.cs"], 2, Root);

        note.ShouldStartWith($"NOTE: 1 of the 2 files entry(s) named no file under the solution root \"{Root}\"");
        note.ShouldContain("\"src/Typo.cs\"");
        note.ShouldContain("Those were not inspected.");
    }

    [Fact]
    public void For_AnEntryThatNamesNoFile_DoesNotVouchForTheOnesItLeavesOut()
    {
        // The limit this server cannot see past: jb matches --include against the files that belong to a
        // project, so a path that resolves here can still match nothing there. The note states that rather
        // than implying the rest of the scope ran as asked.
        string note = InspectScopeNote.For(["src/Typo.cs"], 2, Root);

        note.ShouldContain("A path that does exist can still match nothing");
        note.ShouldContain("belong to a project in the solution");
        note.ShouldNotContain("ran as asked");
    }

    [Fact]
    public void For_MoreMissingEntriesThanTheCap_ListsTenThenCountsTheRest()
    {
        // Arrange — this is a prefix competing with findings for one budget, so it collapses the way the
        // issue listing does. Cleanup's unbounded list is the deliberate divergence: there the call failed
        // and the message is the whole response.
        string[] missing = [.. Enumerable.Range(0, 14).Select(i => $"src/Missing{i:D2}.cs")];

        // Act
        string note = InspectScopeNote.For(missing, 14, Root);

        // Assert
        note.ShouldStartWith("NOTE: 14 of the 14 files entry(s) named no file");
        note.ShouldContain("\"src/Missing00.cs\"");
        note.ShouldContain("\"src/Missing09.cs\"");
        note.ShouldNotContain("\"src/Missing10.cs\"");
        note.ShouldContain("(+4 more)");
    }

    [Fact]
    public void For_EndsWithABlankLine_SoItReadsAsAPreamble()
    {
        // The same separator the other preambles use, so they concatenate into one preamble block.
        InspectScopeNote.For(["src/Typo.cs"], 1, Root).ShouldEndWith("\n\n");
    }
}