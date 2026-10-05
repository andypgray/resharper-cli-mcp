using System.Text.Json;
using NSubstitute;
using NSubstitute.Core;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Tests.Tools;

/// <summary>
///     <see cref="ResharperTools.InspectAsync" /> end to end over the two faked seams, with the config and
///     service graph real.
/// </summary>
public sealed class ResharperToolsInspectTests : IDisposable
{
    private readonly FakeEnvironment _environment = new();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();

    public ResharperToolsInspectTests()
    {
        _environment.PlantSolution("App.sln");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public async Task InspectAsync_SolutionInWorkingDirectory_ReturnsFormattedIssues()
    {
        // Arrange
        string sarif = Fixtures.ReadSarif("inspect-sample.json");
        StubJb(sarif);
        ResharperTools tools = Tools();

        // Act
        string result = await tools.InspectAsync(cancellationToken: Ct);

        // Assert — FakeEnvironment sets no MAX_MCP_OUTPUT_TOKENS, so the budget is the 25,000 default and
        // this ~420-character result renders at Full: the small-batch case is unchanged by the ladder.
        result.ShouldStartWith("Found 3 issue(s)");
        result.ShouldNotContain("--- DETAIL REDUCED ---");
    }

    // Only the non-default values: a Warning row would pass with the argument ignored, since Warning is the
    // default. Rows are names because the internal enum cannot appear in a public [Theory] signature
    // (CS0051). Case-insensitive string input is coerced at the binding layer — see the converter and
    // coercion tests.
    [Theory]
    [InlineData(nameof(InspectSeverity.Suggestion), "--severity=SUGGESTION")]
    [InlineData(nameof(InspectSeverity.Error), "--severity=ERROR")]
    public async Task InspectAsync_NonDefaultSeverity_DrivesTheCliToken(string severityName, string expectedArgument)
    {
        // Arrange
        List<string>? inspectArguments = null;
        StubJb(
            Fixtures.ReadSarif("inspect-sample.json"),
            args => inspectArguments = [.. args]);
        ResharperTools tools = Tools();
        var severity = Enum.Parse<InspectSeverity>(severityName);

        // Act
        await tools.InspectAsync(severity: severity, cancellationToken: Ct);

        // Assert
        inspectArguments.ShouldNotBeNull();
        inspectArguments.ShouldContain(expectedArgument);
    }

    [Fact]
    public async Task InspectAsync_AbsolutePaths_ReachJbRelative()
    {
        // Arrange — the same defect, and the dangerous half: jb exits 0 having matched nothing, so an
        // unmatched absolute path came back as "No issues found." with no error anywhere.
        List<string>? inspectArguments = null;
        StubJb(
            Fixtures.ReadSarif("inspect-sample.json"),
            args => inspectArguments = [.. args]);
        ResharperTools tools = Tools();

        // Act
        await tools.InspectAsync([Path.Combine(_environment.CurrentDirectory, "src", "A.cs")], cancellationToken: Ct);

        // Assert
        inspectArguments.ShouldNotBeNull();
        inspectArguments.ShouldContain("--include=src/A.cs");
    }

    [Fact]
    public async Task InspectAsync_EntryJoiningSeveralGlobs_IsSplitIntoSeparatePatterns()
    {
        // Arrange — the same mistake is worse here: the joined string reaches jb as one pattern that matches
        // nothing, and the tool reports "No issues found." for a scan that never looked at the files asked for.
        List<string>? inspectArguments = null;
        StubJb(
            Fixtures.ReadSarif("inspect-sample.json"),
            args => inspectArguments = [.. args]);
        ResharperTools tools = Tools();

        // Act
        await tools.InspectAsync(["src/**/*.cs,tests/**/*.cs"], cancellationToken: Ct);

        // Assert — jb's own separator is ";", so the two patterns arrive as two.
        inspectArguments.ShouldNotBeNull();
        inspectArguments.ShouldContain("--include=src/**/*.cs;tests/**/*.cs");
    }

    [Fact]
    public async Task InspectAsync_UnreadableSettings_SaysNothingAboutIt()
    {
        // Arrange — the blast radii differ. jb reads that file itself — the adjacent .DotSettings is a
        // layer it mounts on its own — and parses it perfectly well, so inspection severities are
        // unaffected; only this server's own profile lookup failed. Warning here would report a
        // consequence that does not exist.
        DotSettingsFixtures.PlantBeside(_environment.CurrentDirectory, DotSettingsFixtures.Unparseable());
        StubJb(Fixtures.ReadSarif("inspect-sample.json"));
        ResharperTools tools = Tools();

        // Act
        string result = await tools.InspectAsync(cancellationToken: Ct);

        // Assert — "WARNING" alone would match the severity label on an issue line, so this asserts on the
        // banner's own wording.
        result.ShouldNotContain("could not read ReSharper settings");
        result.ShouldStartWith("Found 3 issue(s)");
    }

    [Fact]
    public async Task InspectAsync_JbSettingsPathNamesAMissingFile_LeadsWithAWarning()
    {
        // Arrange — this one does reach inspect: the file the variable names reaches jb neither by flag
        // nor by its own discovery, so the severities it was supposed to carry are absent. On an empty
        // result especially, a bare "No issues found." would read as a clean bill of health for a scan
        // that ran unconfigured.
        string missing = Path.Combine(_environment.CurrentDirectory, "missing.DotSettings");
        _environment.SetVariable("JB_SETTINGS_PATH", missing);
        StubJb(Fixtures.ReadSarif("empty-runs.json"));
        ResharperTools tools = Tools();

        // Act
        string result = await tools.InspectAsync(cancellationToken: Ct);

        // Assert
        result.ShouldBe(
            $"WARNING: JB_SETTINGS_PATH is set to \"{missing}\" but no such file exists, so the ReSharper "
            + "settings it names were not applied to this run.\n\n"
            + "No issues found.");
    }

    [Fact]
    public async Task InspectAsync_SolutionAndProjectLayersDisagree_LeavesSettingsDiscoveryToJb()
    {
        // Arrange — a tree where the project layer narrows a rule the solution layer reports: on a direct
        // jb run ProjectShared outranks SolutionShared, so the project's DO_NOT_SHOW wins. Passing the
        // solution file as --settings would re-mount it as a Custom layer above the project layer and
        // resurrect every finding the project scoped away (measured in the field: 0 findings became 83).
        DotSettingsFixtures.PlantBeside(_environment.CurrentDirectory, DotSettingsFixtures.SettingSeverity("MethodHasAsyncOverload", "WARNING"));
        SolutionFiles.Plant(_environment.CurrentDirectory, "Proj/Proj.csproj");
        SolutionFiles.Plant(_environment.CurrentDirectory, "Proj/Proj.csproj.DotSettings", DotSettingsFixtures.SettingSeverity("MethodHasAsyncOverload", "DO_NOT_SHOW"));
        List<string>? inspectArguments = null;
        StubJb(
            Fixtures.ReadSarif("empty-runs.json"),
            args => inspectArguments = [.. args]);
        ResharperTools tools = Tools();

        // Act
        await tools.InspectAsync(cancellationToken: Ct);

        // Assert — no --settings: the pin that the project layer is left able to win.
        inspectArguments.ShouldNotBeNull();
        inspectArguments.Any(a => a.StartsWith("--settings", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public async Task InspectAsync_AFilesEntryThatNamesNoFile_NamesItAndStillReturns()
    {
        // Arrange — inspect is read-only, and a files scope is measured to buy no time (269 s scoped against
        // 272 s solution-wide), so failing the call would charge a full second run for what a note gives
        // away free. It came back as a bare result before, and the scan silently covered less than asked.
        StubJb(Fixtures.ReadSarif("empty-runs.json"));
        ResharperTools tools = Tools();

        // Act
        string result = await tools.InspectAsync(["src/Typo.cs"], cancellationToken: Ct);

        // Assert
        result.ShouldStartWith("NOTE: 1 of the 1 files entry(s) named no file under the solution root");
        result.ShouldContain("\"src/Typo.cs\"");
        result.ShouldEndWith("No issues found.");
    }

    [Fact]
    public async Task InspectAsync_APartialScope_ReportsTheFindingsAndTheEntryThatMatchedNothing()
    {
        // Arrange — the case jb reports on in no release: one entry matches, jb exits 0 with its findings,
        // and nothing anywhere mentions the other. The all-miss case fails loudly on a current jb; this one
        // never has.
        SolutionFiles.Plant(_environment.CurrentDirectory, "src/A.cs");
        List<string>? inspectArguments = null;
        StubJb(
            Fixtures.ReadSarif("inspect-sample.json"),
            args => inspectArguments = [.. args]);
        ResharperTools tools = Tools();

        // Act
        string result = await tools.InspectAsync(["src/A.cs", "src/Typo.cs"], cancellationToken: Ct);

        // Assert — both entries still reach jb, which is the one that can judge them, and the findings come
        // back under a note naming only the entry this server could rule out.
        inspectArguments.ShouldNotBeNull();
        inspectArguments.ShouldContain("--include=src/A.cs;src/Typo.cs");
        result.ShouldStartWith("NOTE: 1 of the 2 files entry(s) named no file");
        result.ShouldContain("\"src/Typo.cs\"");
        result.ShouldContain("Found 3 issue(s)");
    }

    [Fact]
    public async Task InspectAsync_AScopeThatAllResolves_IsByteIdenticalToTheRunWithoutTheNote()
    {
        // Arrange — the default-path pin. A fourth preamble may not move a byte of what a well-formed
        // scoped call already gets, which is what makes it free to add.
        SolutionFiles.Plant(_environment.CurrentDirectory, "src/A.cs");
        StubJb(Fixtures.ReadSarif("inspect-sample.json"));
        ResharperTools tools = Tools();

        // Act
        string scoped = await tools.InspectAsync(["src/A.cs"], cancellationToken: Ct);
        string wildcard = await tools.InspectAsync(["src/**/*.cs"], cancellationToken: Ct);
        string unscoped = await tools.InspectAsync(cancellationToken: Ct);

        // Assert
        scoped.ShouldBe(unscoped);
        wildcard.ShouldBe(unscoped);
    }

    [Fact]
    public async Task InspectAsync_AMissingScopeEntryAndCompilationErrors_ReadsScopeBeforeResults()
    {
        // Arrange — the preamble block reads before-the-run, then the run's scope, then how to read the
        // results. A scope entry that was never inspected is a fact about the run, so it comes above a note
        // about what came back.
        string cacheHome = _environment.CreateTempDirectory();
        _environment.SetVariable("JB_CACHE_HOME", cacheHome);
        StubJb(Fixtures.ReadSarif("inspect-phantom-errors.json"));
        ResharperTools tools = Tools();

        // Act
        string result = await tools.InspectAsync(["src/Typo.cs"], cancellationToken: Ct);

        // Assert
        result.IndexOf("files entry(s) named no file", StringComparison.Ordinal)
            .ShouldBeLessThan(result.IndexOf("are compilation errors", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InspectAsync_ResultCarriesCompilationErrors_LeadsWithTheStaleCacheNote()
    {
        // Arrange — the incident's shape reaching a real tool result: the note has to be joined onto the
        // banner inside the tool method, or it exists and nobody ever sees it.
        string cacheHome = _environment.CreateTempDirectory();
        _environment.SetVariable("JB_CACHE_HOME", cacheHome);
        StubJb(Fixtures.ReadSarif("inspect-phantom-errors.json"));
        ResharperTools tools = Tools();

        // Act
        string result = await tools.InspectAsync(cancellationToken: Ct);

        // Assert — the discriminator, the cure, and the resolved cache home the caller could not derive.
        result.ShouldStartWith("NOTE: 2 of these issue(s) are compilation errors (`.CSharpErrors`).");
        result.ShouldContain("Build the solution first");
        result.ShouldContain(ResharperTools.ResetCacheToolName);
        result.ShouldContain($"under \"{cacheHome}\"");
        result.ShouldContain("Found 3 issue(s) across 2 file(s)");
    }

    [Fact]
    public async Task InspectAsync_CompilationErrorsAndASqueezedBudget_KeepsTheNoteDownToMinimal()
    {
        // Arrange — the case the note exists for is a wall of phantom errors, which is exactly the result too
        // big to render in full. Charging the note to the budget before rendering is what makes it survive
        // every reduction step instead of vanishing at the level that fires.
        _environment.SetVariable("MAX_MCP_OUTPUT_TOKENS", "300"); // 750 characters
        string cacheHome = _environment.CreateTempDirectory();
        _environment.SetVariable("JB_CACHE_HOME", cacheHome);
        StubJb(ManyIssuesSarif(200));
        ResharperTools tools = Tools();

        // Act
        string result = await tools.InspectAsync(cancellationToken: Ct);

        // Assert — reduced all the way to the one-liner with the note whole, both ends of it. At a budget this
        // small ResponseTruncator's MinimumBodyChars floor engages and the pair can run a few characters over
        // the cap; that is the backstop's business, and it cuts from the tail, so the note is the last thing
        // that could ever be lost.
        result.ShouldStartWith("NOTE: 1 of these issue(s) are compilation errors");
        result.ShouldContain("See the resharper://guides/setup resource.");
        result.ShouldContain("totals, severity counts, and the top rules only.");
        result.ShouldContain("Found 201 issue(s) across 201 file(s)");
    }

    // The report parameter is an internal enum, so every case below is a Fact with the value as a body
    // literal — the same CS0051 constraint the severity pair above is written around.
    [Fact]
    public async Task InspectAsync_NoReportAsked_WritesNothingAndReturnsWhatItAlwaysDid()
    {
        // Arrange — the default. Nothing about a response without the parameter may change, which is what
        // makes the report free for every caller that does not want one.
        string reportRoot = _environment.CreateTempDirectory();
        StubJb(Fixtures.ReadSarif("inspect-sample.json"));
        ResharperTools tools = Tools(reportRoot);

        // Act
        string result = await tools.InspectAsync(cancellationToken: Ct);

        // Assert
        result.ShouldStartWith("Found 3 issue(s)");
        result.ShouldNotContain("FULL REPORT");
        Directory.Exists(Path.Combine(reportRoot, InspectReportWriter.ReportsDirectoryName)).ShouldBeFalse();
    }

    [Fact]
    public async Task InspectAsync_ReportMarkdown_NamesAFileCarryingTheRunAndEveryMessage()
    {
        // Arrange
        string reportRoot = _environment.CreateTempDirectory();
        StubJb(Fixtures.ReadSarif("inspect-sample.json"));
        ResharperTools tools = Tools(reportRoot);

        // Act
        string result = await tools.InspectAsync(severity: InspectSeverity.Suggestion, report: InspectReport.Markdown, cancellationToken: Ct);

        // Assert — the note leads, and the path it names holds the provenance a file read later cannot
        // reconstruct plus the Full listing.
        result.ShouldStartWith("FULL REPORT: all 3 issue(s)");
        string reportPath = PathFromNote(result);
        File.Exists(reportPath).ShouldBeTrue();
        string document = File.ReadAllText(reportPath);
        document.ShouldStartWith("# ReSharper inspection report\n");
        document.ShouldContain("- Minimum severity: SUGGESTION");
        document.ShouldContain("- Scope: whole solution");
        document.ShouldContain("Found 3 issue(s)");
    }

    [Fact]
    public async Task InspectAsync_ReportMarkdownAndNothingFound_StillWritesTheFile()
    {
        // Arrange — "a report was asked for, so the response names a file that exists" is a contract a caller
        // can script against; one that sometimes yields no file is not.
        string reportRoot = _environment.CreateTempDirectory();
        StubJb(Fixtures.ReadSarif("empty-runs.json"));
        ResharperTools tools = Tools(reportRoot);

        // Act
        string result = await tools.InspectAsync(report: InspectReport.Markdown, cancellationToken: Ct);

        // Assert — the file is named without claiming the response was reduced to fit a budget, since there
        // is no listing for it to hold more of. What it carries is the provenance header: a dated clean bill
        // of health for this solution at this severity over this scope.
        result.ShouldStartWith("FULL REPORT: no issues found; the run and its scope were written to");
        result.ShouldEndWith("No issues found.");
        string document = File.ReadAllText(PathFromNote(result));
        document.ShouldContain("No issues found.");
        document.ShouldContain("- Scope: whole solution");
    }

    [Fact]
    public async Task InspectAsync_ReportMarkdownAndASqueezedBudget_KeepsTheNoteAndHoldsWhatTheResponseDropped()
    {
        // Arrange — the case the parameter exists for. The response is reduced to the one-liner while the
        // file keeps every finding, and the note naming that file has to survive the whole ladder or it
        // vanishes exactly when it matters.
        _environment.SetVariable("MAX_MCP_OUTPUT_TOKENS", "300"); // 750 characters
        string reportRoot = _environment.CreateTempDirectory();
        StubJb(ManyIssuesSarif(200));
        ResharperTools tools = Tools(reportRoot);

        // Act
        string result = await tools.InspectAsync(report: InspectReport.Markdown, cancellationToken: Ct);

        // Assert — this SARIF also trips the compilation-error note, so both preambles are present and the
        // report note is the last of them, sitting immediately above the listing it refers to.
        result.ShouldStartWith("NOTE: 1 of these issue(s) are compilation errors");
        result.ShouldContain("FULL REPORT: all 201 issue(s)");
        result.IndexOf("FULL REPORT", StringComparison.Ordinal)
            .ShouldBeLessThan(result.IndexOf("Found 201 issue(s)", StringComparison.Ordinal));
        result.ShouldContain("totals, severity counts, and the top rules only.");

        // Having written the file, the reduction note does not go on to suggest writing one.
        result.ShouldNotContain(IssueMarkdownFormatter.FullReportHint);

        string document = File.ReadAllText(PathFromNote(result));
        document.ShouldContain("File000.cs");
        document.ShouldContain("File199.cs");
        result.ShouldNotContain("File199.cs"); // the response could not carry what the file does
    }

    // detail is the third internal enum on the surface, so the same CS0051 constraint applies: every case
    // is a Fact with the value as a body literal.
    [Fact]
    public async Task InspectAsync_DetailLowOnAResultThatFitsAtFull_ReturnsTheRollupAndSaysItWasAskedFor()
    {
        // Arrange — 3 issues render at Full well inside the default 25,000-character budget, so nothing
        // about this response is the budget's doing. Before the parameter, overflowing was the only way in.
        StubJb(Fixtures.ReadSarif("inspect-sample.json"));
        ResharperTools tools = Tools();

        // Act
        string result = await tools.InspectAsync(detail: InspectDetail.Low, cancellationToken: Ct);

        // Assert — the rollup, under a note that does not blame a limit nothing came near, and without the
        // remedy that would tell a caller to do what it just did.
        result.ShouldContain("By rule (");
        result.ShouldContain("By file (");
        result.ShouldContain("Rendered at the requested detail level Low");
        result.ShouldNotContain("character limit");
        result.ShouldNotContain(IssueMarkdownFormatter.NarrowingHint);
    }

    [Fact]
    public async Task InspectAsync_DetailLowAndABudgetTooSmallForIt_StepsBelowTheCapAndNamesTheLimit()
    {
        // Arrange — a cap is not a floor. 201 issues do not roll up inside 750 characters, so the ladder
        // keeps stepping and the note has to stop claiming the level was the caller's choice.
        _environment.SetVariable("MAX_MCP_OUTPUT_TOKENS", "300"); // 750 characters
        StubJb(ManyIssuesSarif(200));
        ResharperTools tools = Tools();

        // Act
        string result = await tools.InspectAsync(detail: InspectDetail.Low, cancellationToken: Ct);

        // Assert — below the cap the budget decided, so both the limit and the narrowing remedy come back.
        result.ShouldContain("Output exceeded the");
        result.ShouldContain("Reduced to Minimal");
        result.ShouldContain(IssueMarkdownFormatter.NarrowingHint);
    }

    [Fact]
    public async Task InspectAsync_DetailFull_IsByteIdenticalToTheCallThatPassesNoDetail()
    {
        // Arrange — the default-path pin. A fifth parameter may not move a byte of what every existing
        // caller already gets, which is what makes it free to add.
        StubJb(Fixtures.ReadSarif("inspect-sample.json"));
        ResharperTools tools = Tools();

        // Act
        string withoutDetail = await tools.InspectAsync(cancellationToken: Ct);
        string withFull = await tools.InspectAsync(detail: InspectDetail.Full, cancellationToken: Ct);

        // Assert
        withFull.ShouldBe(withoutDetail);
        withFull.ShouldNotContain("--- DETAIL REDUCED ---");
    }

    [Fact]
    public async Task InspectAsync_EveryDetailValue_SettlesAtTheLevelItNames()
    {
        // Arrange — a 3-issue result fits at every level, so where a response lands is the parameter's
        // doing alone. The note is asserted against the enum member's own name, which is also what pins
        // the tool-facing enum and the formatting ladder to the same spellings. Full is the no-cap
        // default and comes back verbatim with nothing to announce, which DetailFull_IsByteIdentical pins.
        StubJb(Fixtures.ReadSarif("inspect-sample.json"));
        ResharperTools tools = Tools();
        IEnumerable<InspectDetail> cappingDetails = Enum.GetValues<InspectDetail>()
            .Where(detail => detail != InspectDetail.Full);

        // Act / Assert
        foreach (InspectDetail detail in cappingDetails)
        {
            string result = await tools.InspectAsync(detail: detail, cancellationToken: Ct);

            result.ShouldContain($"Rendered at the requested detail level {detail}");
        }
    }

    [Fact]
    public async Task InspectAsync_DetailMinimalAndReportMarkdown_AnswersInOneLineAndFilesEveryFinding()
    {
        // Arrange — the composition the pair exists for, and the survey-a-legacy-solution case in one
        // call: a cheap verdict in the response while every finding stays reachable in the file. Neither
        // parameter delivers it alone.
        string reportRoot = _environment.CreateTempDirectory();
        StubJb(ManyIssuesSarif(200));
        ResharperTools tools = Tools(reportRoot);

        // Act
        string result = await tools.InspectAsync(
            report: InspectReport.Markdown, detail: InspectDetail.Minimal, cancellationToken: Ct);

        // Assert — the one-liner, at a level the response says was asked for rather than forced.
        result.ShouldContain("Rendered at the requested detail level Minimal");
        result.ShouldContain("Found 201 issue(s) across 201 file(s).");
        result.ShouldNotContain("File199.cs");

        // Neither remedy fires: a report was written, and the caller chose this level.
        result.ShouldNotContain(IssueMarkdownFormatter.FullReportHint);
        result.ShouldNotContain(IssueMarkdownFormatter.NarrowingHint);

        string document = File.ReadAllText(PathFromNote(result));
        document.ShouldContain("File000.cs");
        document.ShouldContain("File199.cs");
    }

    [Fact]
    public async Task InspectAsync_DetailMinimalAndNothingFound_ReturnsOnlyTheOneLine()
    {
        // Arrange — the gap that let the defect ship: nothing pinned a zero-issue run at a capped level. The
        // formatter answers "No issues found." at all five, so passing detail reduced nothing, and the note
        // went on to describe a collapse of a listing that never existed and offer a report file for findings
        // there are none of.
        StubJb(Fixtures.ReadSarif("empty-runs.json"));
        ResharperTools tools = Tools();

        // Act
        string result = await tools.InspectAsync(detail: InspectDetail.Minimal, cancellationToken: Ct);

        // Assert
        result.ShouldBe("No issues found.");
    }

    [Fact]
    public async Task InspectAsync_DetailMinimalReportAndNothingFound_NamesTheFileAndNothingElse()
    {
        // Arrange — the same run with a report asked for. The file is still written and still named; what
        // goes is the claim that the listing below was rendered to fit a budget.
        string reportRoot = _environment.CreateTempDirectory();
        StubJb(Fixtures.ReadSarif("empty-runs.json"));
        ResharperTools tools = Tools(reportRoot);

        // Act
        string result = await tools.InspectAsync(
            report: InspectReport.Markdown, detail: InspectDetail.Minimal, cancellationToken: Ct);

        // Assert
        result.ShouldStartWith("FULL REPORT: no issues found; the run and its scope were written to");
        result.ShouldEndWith("No issues found.");
        result.ShouldNotContain("--- DETAIL REDUCED ---");
        File.Exists(PathFromNote(result)).ShouldBeTrue();
    }

    [Fact]
    public async Task InspectAsync_DetailValueThatIsNoMember_FailsBeforeSpendingAJbRun()
    {
        // Arrange — unreachable through the binder, which rejects anything not a member by name. It is
        // reachable by a member added to InspectDetail and not to the mapping, and the reason the two enums
        // are mapped by hand is that such a gap must not quietly resolve to a plausible-looking level.
        StubJb(Fixtures.ReadSarif("inspect-sample.json"));
        ResharperTools tools = Tools();

        // Act
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => tools.InspectAsync(detail: (InspectDetail)99, cancellationToken: Ct));

        // Assert — and it fails ahead of the analysis, so a bad argument never costs the minutes a run does.
        await _processRunner.DidNotReceive().AnyRunWith(args => args != null && args.Count > 0 && args[0] == "inspectcode");
    }

    [Fact]
    public async Task InspectAsync_SolutionPathThatDoesNotExist_StillFails()
    {
        // Arrange — the relaxation is the reset's alone. An analysis tool handed a path with no file on it
        // has nothing to analyse, and letting it through would trade a clear error for a jb failure.
        using FakeEnvironment environment = new();
        StubJb();
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);
        string missing = environment.CreateSolutionPath("Gone.sln");

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => tools.InspectAsync(solutionPath: missing, cancellationToken: Ct));

        // Assert
        exception.Message.ShouldBe($"Specified solution path \"{missing}\" does not exist.");
    }

    [Fact]
    public async Task InspectAsync_NoSolutionInWorkingDirectory_ThrowsUserErrorMentioningJbSolutionPath()
    {
        // Arrange — the working directory is an empty temp dir, so discovery finds no solution.
        using FakeEnvironment environment = new();
        StubJb();
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => tools.InspectAsync(cancellationToken: Ct));

        // Assert
        exception.Message.ShouldContain("JB_SOLUTION_PATH");
    }

    /// <summary>The tool methods over this class's environment and process runner.</summary>
    private ResharperTools Tools(string? reportRoot = null)
    {
        return ToolHarness.Build(_processRunner, _environment, reportRoot: reportRoot);
    }

    /// <summary>
    ///     Routes the single process-runner substitute by jb sub-command: the version probe, or an
    ///     <c>inspectcode</c> run, which writes <paramref name="inspectSarif" /> to its <c>-o=</c> path. Every
    ///     run succeeds the way a healthy one does, leaving its cache generation behind.
    /// </summary>
    private void StubJb(string inspectSarif = "", Action<IReadOnlyList<string>>? onInspect = null)
    {
        _processRunner
            .AnyRun()
            .Returns(Route);

        return;

        ProcessResult Route(CallInfo callInfo)
        {
            IReadOnlyList<string> arguments = callInfo.Arguments();

            if (JbStubs.IsVersionProbe(arguments)) return JbStubs.VersionProbeAnswer;

            if (!JbStubs.IsRunOf(arguments, "inspectcode")) return JbStubs.Succeed(arguments);

            onInspect?.Invoke(arguments);
            return JbStubs.Succeed(arguments, inspectSarif);
        }
    }

    /// <summary>
    ///     A SARIF document with one compilation error and <paramref name="warnings" /> ordinary warnings, each
    ///     in its own long-pathed file — enough files that the issue listing cannot fit a squeezed budget at
    ///     any level above Minimal. Generated rather than a fixture: the only thing that matters about it is
    ///     its size, and a 200-result JSON file would be unreadable to a maintainer.
    /// </summary>
    private static string ManyIssuesSarif(int warnings)
    {
        List<object> results =
        [
            Result("CSharpErrors", "error", "Cannot resolve symbol 'DllPath'", "src/very/long/path/to/Consumer.cs", 12)
        ];

        for (var i = 0; i < warnings; i++)
            results.Add(Result(
                "RedundantUsingDirective", "warning", "Using directive is not required by the code",
                $"src/very/long/path/to/generated/File{i:D3}.cs", i + 1));

        return JsonSerializer.Serialize(new { version = "2.1.0", runs = new[] { new { results } } });

        static object Result(string ruleId, string level, string message, string path, int line)
        {
            return new
            {
                ruleId,
                level,
                message = new { text = message },
                locations = new[]
                {
                    new
                    {
                        physicalLocation = new
                        {
                            artifactLocation = new { uri = $"file:///C:/work/AppSample/{path}" },
                            region = new { startLine = line }
                        }
                    }
                }
            };
        }
    }

    /// <summary>
    ///     The report path out of the response's preamble, read the way an agent would. Anchored on the
    ///     report note's own wording rather than on the first quotation mark in the response: the
    ///     compilation-error note leads when it applies, and it quotes the cache home.
    /// </summary>
    private static string PathFromNote(string result)
    {
        const string anchor = "written to \"";
        int start = result.IndexOf(anchor, StringComparison.Ordinal) + anchor.Length;
        int end = result.IndexOf('"', start);

        return result[start..end];
    }
}