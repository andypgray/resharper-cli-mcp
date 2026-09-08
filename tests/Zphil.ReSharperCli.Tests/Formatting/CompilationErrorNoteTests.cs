using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Resources;
using Zphil.ReSharperCli.Sarif;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Tests.Formatting;

/// <summary>
///     <see cref="CompilationErrorNote" /> exists because a stale ReSharper index cost a whole session of
///     forensics once. These pin the two halves of getting that right: it fires on the one rule that carries
///     the symptom, and it says nothing at all otherwise — a note on every ordinary inspect result would be
///     noise charged to every response's budget.
///     <para>
///         The text itself is pinned whole, because the ordering is the feature: the build leads, the
///         never-restored origin is named with its mechanism, and the cache reset is conditional on the
///         discriminator's answer rather than an imperative. A length pin sits beside it, since this note is
///         charged to every affected response before the ladder runs.
///     </para>
/// </summary>
public sealed class CompilationErrorNoteTests
{
    private const string CacheHome = "/home/u/.jb-cache";

    [Fact]
    public void For_CompilationErrors_LeadsWithTheBuildAndGatesTheResetOnIt()
    {
        // Arrange — the incident's shape: a resolution failure and its knock-on ambiguity, in a file the
        // author never touched, beside an ordinary warning. The rule id carries jb's leading dot, as taken
        // from a real 2026.1.2 SARIF document rather than assumed.
        List<InspectIssue> issues = Issues(
            (".CSharpErrors", "Cannot resolve symbol 'DllPath'"),
            (".CSharpErrors", "Ambiguous invocation: Path.GetFileName"),
            ("RedundantUsingDirective", "Using directive is not required"));

        // Act
        string note = CompilationErrorNote.For(issues, CacheHome);

        // Assert — the exact text, because this is the whole feature. The cure's tool name and the guide's
        // URI are interpolated from their owners, pinning that the note routes to names that really exist.
        note.ShouldBe(
            "NOTE: 2 of these issue(s) are compilation errors (`.CSharpErrors`). Build the solution first: on "
            + "a checkout that was never built or never restored, every unrestored package reference reports "
            + "here. If the build succeeds and an inspect still reports them, they are phantoms from a stale "
            + $"ReSharper index; run {ResharperTools.ResetCacheToolName} to drop this solution's cache "
            + $"generation under \"{CacheHome}\", which costs the next call a cold analysis. "
            + $"See the {ResharperResources.SetupGuideUri} resource.\n\n");
    }

    [Fact]
    public void For_CompilationErrors_NamesTheNeverRestoredOriginFirstAndGatesTheResetOnTheBuild()
    {
        // A fresh worktree inspected before a build reported 13,990 compilation errors out of 17,971
        // findings, and every one of them was real. The note used to spend three of its four sentences on
        // the stale-index branch and close with the reset as an unconditional imperative — which on that
        // checkout drops the generation, blocks seeding from a sibling, and buys a cold rebuild on top of
        // the build that was needed anyway. These are the properties of the text a future edit must keep:
        // the cheap branch is named with its mechanism and comes first; the reset is gated on the
        // discriminator's answer rather than arriving as an imperative; and the cold analysis it costs is
        // stated rather than left for the caller to discover on the next call.
        string note = NoteForOneError();

        note.ShouldContain("never built or never restored, every unrestored package reference reports here");
        note.IndexOf("unrestored package", StringComparison.Ordinal)
            .ShouldBeLessThan(note.IndexOf("stale", StringComparison.Ordinal));
        note.ShouldContain("If the build succeeds and an inspect still reports them");
        note.ShouldContain("costs the next call a cold analysis");
        note.IndexOf("If the build succeeds", StringComparison.Ordinal)
            .ShouldBeLessThan(note.IndexOf(ResharperTools.ResetCacheToolName, StringComparison.Ordinal));
    }

    [Fact]
    public void For_CompilationErrors_StaysWithinTheBudgetThisNoteIsWorth()
    {
        // This note is charged to every affected response before the ladder runs, so every character it
        // spends is one the findings do not get. 500 was the ceiling the rewrite was written against, and
        // nothing recorded it until now — which is to say the next edit would have spent it.
        string note = NoteForOneError();

        // Assert — the cache home is the caller's, so measure the note without it.
        (note.Length - CacheHome.Length).ShouldBeLessThanOrEqualTo(500);
    }

    /// <summary>The note for a result carrying one compilation error, which is all its text turns on.</summary>
    private static string NoteForOneError()
    {
        List<InspectIssue> issues = Issues((".CSharpErrors", "Cannot resolve symbol 'JsonSerializer'"));
        return CompilationErrorNote.For(issues, CacheHome);
    }

    [Fact]
    public void For_NoCompilationErrors_SaysNothing()
    {
        // Arrange — the overwhelmingly common result.
        List<InspectIssue> issues = Issues(
            ("RedundantUsingDirective", "Using directive is not required"),
            ("UnusedMember.Global", "Method 'Total' is never used"));

        // Act & Assert — empty rather than blank, so the banner concatenation adds no separator either.
        CompilationErrorNote.For(issues, CacheHome).ShouldBeEmpty();
    }

    [Fact]
    public void For_NoIssuesAtAll_SaysNothing()
    {
        CompilationErrorNote.For([], CacheHome).ShouldBeEmpty();
    }

    [Fact]
    public void For_TheUndottedSpellingOfTheRule_StillFires()
    {
        // Arrange — nothing documents jb's leading dot, so the note matches the bare id too rather than
        // going silent if a release ever drops it.
        List<InspectIssue> issues = Issues(("CSharpErrors", "Cannot resolve symbol 'DllPath'"));

        // Act & Assert
        CompilationErrorNote.For(issues, CacheHome).ShouldStartWith("NOTE: 1 of these issue(s)");
    }

    [Fact]
    public void For_ARuleThatMerelyResemblesTheErrorRule_SaysNothing()
    {
        // Arrange — matching is exact and ordinal beyond that one optional dot. A near-miss firing the note
        // would send an agent to delete its cache over an ordinary warning.
        List<InspectIssue> issues = Issues(
            ("CSharpWarnings::CS0168", "Variable is declared but never used"),
            ("csharperrors", "lower case is a different rule id"),
            ("CSharpErrors.Global", "a suffixed id is a different rule"));

        // Act & Assert
        CompilationErrorNote.For(issues, CacheHome).ShouldBeEmpty();
    }

    [Fact]
    public void For_EndsWithABlankLine_SoItReadsAsAPreamble()
    {
        // Assert — the same separator ConfigWarningBanner uses, so the two concatenate into one preamble
        // block rather than running into each other or into the body.
        List<InspectIssue> issues = Issues((".CSharpErrors", "Cannot resolve symbol 'DllPath'"));

        CompilationErrorNote.For(issues, CacheHome).ShouldEndWith("\n\n");
    }

    private static List<InspectIssue> Issues(params (string RuleId, string Message)[] issues)
    {
        return [.. issues.Select(issue => new InspectIssue("src/File.cs", 12, null, "ERROR", issue.RuleId, issue.Message))];
    }
}