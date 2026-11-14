using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Resources;
using Zphil.ReSharperCli.Sarif;
using Zphil.ReSharperCli.Tests.TestSupport;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Tests.Formatting;

/// <summary>
///     Pins <see cref="CompilationErrorNote" />'s text, and that it fires on the one rule that carries
///     compilation errors and says nothing at all otherwise: a note on every ordinary inspect result would be
///     noise charged to every response's budget.
/// </summary>
public sealed class CompilationErrorNoteTests
{
    private const string CacheHome = "/home/u/.jb-cache";

    private const string JbVersion = "2026.2.4.1";

    private const string UpdateCommand = "dotnet tool update -g JetBrains.ReSharper.GlobalTools";

    [Fact]
    public void For_CompilationErrors_LeadsWithTheBuildAndGatesTheResetOnIt()
    {
        // Arrange — the symptom as it arrives: a resolution failure and its knock-on ambiguity, in a file the
        // author never touched, beside an ordinary warning. The rule id carries the leading dot jb's SARIF
        // gives it.
        List<InspectIssue> issues = Issues(
            (".CSharpErrors", "Cannot resolve symbol 'DllPath'"),
            (".CSharpErrors", "Ambiguous invocation: Path.GetFileName"),
            ("RedundantUsingDirective", "Using directive is not required"));

        // Act
        string note = CompilationErrorNote.For(issues, CacheHome, JbVersion);

        // Assert — the exact text, because the text is the feature. The cure's tool name and the guide's URI
        // are interpolated from their owners, pinning that the note routes to names that exist.
        note.ShouldBe(
            "NOTE: 2 of these issue(s) are compilation errors (`.CSharpErrors`). Build the solution first: on "
            + "a checkout that was never built or never restored, every unrestored package reference reports "
            + "here. If the build succeeds and an inspect still reports them, they are phantoms. If this jb "
            + $"({JbVersion}) predates the project's C# version, it misreads the newer syntax: update it "
            + $"(as a global tool, `{UpdateCommand}`). Otherwise the ReSharper index is stale: run "
            + $"{ResharperTools.ResetCacheToolName} to drop this solution's cache generation under "
            + $"\"{CacheHome}\", which costs the next call a cold analysis. "
            + $"See the {ResharperResources.SetupGuideUri} resource.\n\n");
    }

    [Fact]
    public void For_CompilationErrorsSurvivingTheBuild_OffersTheJbUpdateBeforeTheReset()
    {
        // A jb older than the project's C# version reports the newer syntax as compilation errors that a clean
        // build does not, which is the same signature a stale index gives. A reset cannot cure it and costs a
        // cold analysis, so the update is named first, with the version an agent compares against, and the
        // reset is left as the other branch. The cure is to update the jb that ran, which the locator may have
        // found on PATH rather than as the global tool, so the global-tool command is offered as one case of it.
        string note = NoteForOneError();

        note.ShouldContainInOrder("If the build succeeds", $"jb ({JbVersion}) predates", "update it (as a global tool, ",
            UpdateCommand, "Otherwise", ResharperTools.ResetCacheToolName);
    }

    [Fact]
    public void For_AnUnknownJbVersion_StillOffersTheUpdate()
    {
        // A located jb always reports a version, but a config built without one leaves it unset, which
        // ResolvedConfig allows; the note then names the cause and its cure without a number.
        // Named because the name says what the null stands for.
        // ReSharper disable once ArgumentsStyleLiteral
        string note = NoteForOneError(jbVersion: null);

        note.ShouldContain(
            $"If this jb predates the project's C# version, it misreads the newer syntax: update it (as a global "
            + $"tool, `{UpdateCommand}`).");
    }

    [Fact]
    public void For_CompilationErrors_NamesTheNeverRestoredOriginFirstAndGatesTheResetOnTheBuild()
    {
        // On a checkout that was never built the compilation errors are real, often most of the findings,
        // and an unconditional reset there drops the generation, blocks seeding from a sibling, and buys a
        // cold rebuild on top of the build that was needed anyway. These are the properties of the text a
        // future edit must keep: the cheap branch is named with its mechanism and comes first; the reset is
        // gated on the discriminator's answer rather than arriving as an imperative; and the cold analysis it
        // costs is stated rather than left for the caller to discover on the next call.
        string note = NoteForOneError();

        note.ShouldContain("never built or never restored, every unrestored package reference reports here");
        note.ShouldContainInOrder("unrestored package", "stale");
        note.ShouldContain("If the build succeeds and an inspect still reports them");
        note.ShouldContain("costs the next call a cold analysis");
    }

    [Fact]
    public void For_CompilationErrors_StaysWithinTheBudgetThisNoteIsWorth()
    {
        // This note is charged to every affected response before the ladder runs, so every character it
        // spends is one the findings do not get. 650 is the ceiling the text is written to; without this pin
        // the next edit spends it.
        string note = NoteForOneError();

        // Assert — the cache home and the jb version are the caller's, so measure the note without them.
        (note.Length - CacheHome.Length - JbVersion.Length).ShouldBeLessThanOrEqualTo(650);
    }

    /// <summary>
    ///     Returns the note for a result carrying one compilation error, which with the jb version is all its
    ///     text turns on.
    /// </summary>
    private static string NoteForOneError(string? jbVersion = JbVersion)
    {
        List<InspectIssue> issues = Issues((".CSharpErrors", "Cannot resolve symbol 'JsonSerializer'"));
        return CompilationErrorNote.For(issues, CacheHome, jbVersion);
    }

    [Theory]
    // The common result: ordinary rules.
    [InlineData("RedundantUsingDirective")]
    [InlineData("UnusedMember.Global")]
    // Near misses. Matching is exact and ordinal beyond the one optional dot, and a near miss firing the note
    // would send an agent to delete its cache over an ordinary warning.
    [InlineData("CSharpWarnings::CS0168")]
    [InlineData("csharperrors")] // lower case is a different rule id
    [InlineData("CSharpErrors.Global")] // a suffixed id is a different rule
    public void For_ARuleOtherThanTheErrorRule_SaysNothing(string ruleId)
    {
        // Arrange
        List<InspectIssue> issues = Issues((ruleId, "an ordinary finding"));

        // Act & Assert — empty rather than blank, so the banner concatenation adds no separator either.
        CompilationErrorNote.For(issues, CacheHome, JbVersion).ShouldBeEmpty();
    }

    [Fact]
    public void For_NoIssuesAtAll_SaysNothing()
    {
        CompilationErrorNote.For([], CacheHome, JbVersion).ShouldBeEmpty();
    }

    [Fact]
    public void For_TheUndottedSpellingOfTheRule_StillFires()
    {
        // Arrange — nothing documents jb's leading dot, so the note matches the bare id too rather than
        // going silent if a release ever drops it.
        List<InspectIssue> issues = Issues(("CSharpErrors", "Cannot resolve symbol 'DllPath'"));

        // Act & Assert
        CompilationErrorNote.For(issues, CacheHome, JbVersion).ShouldStartWith("NOTE: 1 of these issue(s)");
    }

    [Fact]
    public void For_EndsWithABlankLine_SoItReadsAsAPreamble()
    {
        // Assert — the same separator ConfigWarningBanner uses, so the two concatenate into one preamble
        // block rather than running into each other or into the body.
        List<InspectIssue> issues = Issues((".CSharpErrors", "Cannot resolve symbol 'DllPath'"));

        CompilationErrorNote.For(issues, CacheHome, JbVersion).ShouldEndWith("\n\n");
    }

    private static List<InspectIssue> Issues(params (string RuleId, string Message)[] issues)
    {
        return [.. issues.Select(issue => new InspectIssue("src/File.cs", 12, null, "ERROR", issue.RuleId, issue.Message))];
    }
}