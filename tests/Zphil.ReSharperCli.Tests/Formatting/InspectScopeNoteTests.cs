using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Formatting;

namespace Zphil.ReSharperCli.Tests.Formatting;

/// <summary>
///     <see cref="InspectScopeNote" /> is what stops a scoped inspect answering for files it never looked
///     at. These pin the two halves the other preambles are held to as well: it fires on entries the tool
///     found to resolve to nothing, and it says nothing at all otherwise — a note on every scoped call
///     would be noise charged to every response's budget. The classification itself is
///     <c>FilePathList.FindMissing</c>'s and is pinned beside it; this class is handed its answer and reads
///     nothing from disk.
///     <para>
///         The half it deliberately does <em>not</em> claim is pinned too. A path that exists but belongs to
///         no project matches nothing in <c>jb</c> and resolves perfectly well here, so the note never
///         vouches for the entries it leaves out.
///     </para>
/// </summary>
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
        // The partial case, which is the one jb never reports on either version: one entry matches, jb
        // exits 0 with its findings, and nothing anywhere mentions the other.
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
        // The same separator its three neighbours use, so the four concatenate into one preamble block.
        InspectScopeNote.For(["src/Typo.cs"], 1, Root).ShouldEndWith("\n\n");
    }
}