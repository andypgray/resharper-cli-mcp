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
/// </summary>
public sealed class CompilationErrorNoteTests
{
    private const string CacheHome = "/home/u/.jb-cache";

    [Fact]
    public void For_CompilationErrors_LeadsWithTheDiscriminatorAndNamesTheCacheHome()
    {
        // Arrange — the incident's shape: a resolution failure and its knock-on ambiguity, in a file the
        // author never touched, beside an ordinary warning. The rule id carries jb's leading dot, as taken
        // from a real 2026.1.2 SARIF document rather than assumed.
        var issues = Issues(
            (".CSharpErrors", "Cannot resolve symbol 'DllPath'"),
            (".CSharpErrors", "Ambiguous invocation: Path.GetFileName"),
            ("RedundantUsingDirective", "Using directive is not required"));

        // Act
        string note = CompilationErrorNote.For(issues, CacheHome);

        // Assert — the exact text, because this is the whole feature. The cure's tool name and the guide's
        // URI are interpolated from their owners, pinning that the note routes to names that really exist.
        note.ShouldBe(
            "NOTE: 2 of these issue(s) are compilation errors (`.CSharpErrors`). Build the solution before "
            + "acting on them: if the compiler accepts the code, ReSharper's solution-wide index is stale and "
            + $"these are phantoms that will repeat on every re-run. Run {ResharperTools.ResetCacheToolName} to drop this "
            + $"solution's cache generation under \"{CacheHome}\", then inspect again. "
            + $"See the {ResharperResources.SetupGuideUri} resource.\n\n");
    }

    [Fact]
    public void For_NoCompilationErrors_SaysNothing()
    {
        // Arrange — the overwhelmingly common result.
        var issues = Issues(
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
        var issues = Issues(("CSharpErrors", "Cannot resolve symbol 'DllPath'"));

        // Act & Assert
        CompilationErrorNote.For(issues, CacheHome).ShouldStartWith("NOTE: 1 of these issue(s)");
    }

    [Fact]
    public void For_ARuleThatMerelyResemblesTheErrorRule_SaysNothing()
    {
        // Arrange — matching is exact and ordinal beyond that one optional dot. A near-miss firing the note
        // would send an agent to delete its cache over an ordinary warning.
        var issues = Issues(
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
        var issues = Issues((".CSharpErrors", "Cannot resolve symbol 'DllPath'"));

        CompilationErrorNote.For(issues, CacheHome).ShouldEndWith("\n\n");
    }

    private static List<InspectIssue> Issues(params (string RuleId, string Message)[] issues)
    {
        return [.. issues.Select(issue => new InspectIssue("src/File.cs", 12, null, "ERROR", issue.RuleId, issue.Message))];
    }
}