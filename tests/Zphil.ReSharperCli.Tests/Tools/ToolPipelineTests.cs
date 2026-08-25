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
///     End-to-end through the tool methods over the two faked seams: a tool call probes jb, resolves the
///     solution from the working directory, runs jb, and shapes the result. jb is faked; the config +
///     service graph is real.
/// </summary>
public sealed class ToolPipelineTests
{
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InspectAsync_SolutionInWorkingDirectory_ReturnsFormattedIssues()
    {
        // Arrange
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        string sarif = Fixtures.ReadSarif("inspect-sample.json");
        StubJb(sarif);
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        string result = await tools.InspectAsync(cancellationToken: Ct);

        // Assert — FakeEnvironment sets no MAX_MCP_OUTPUT_TOKENS, so the budget is the 25,000 default and
        // this ~420-character result renders at Full: the small-batch case is unchanged by the ladder.
        result.ShouldStartWith("Found 3 issue(s)");
        result.ShouldNotContain("--- DETAIL REDUCED ---");
    }

    // The enum is used as a body literal, not a public method parameter: the internal InspectSeverity
    // cannot appear in a public [Theory] signature (CS0051), so these are two facts. Case-insensitive
    // string INPUT (e.g. "warning") is now coerced/validated at the binding layer — see the converter
    // and coercion tests. These pin the enum → jb CLI-token mapping (and that a non-default value drives it).
    [Fact]
    public async Task InspectAsync_WarningSeverity_MapsToWarningCliToken()
    {
        // Arrange
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        List<string>? inspectArguments = null;
        StubJb(
            Fixtures.ReadSarif("inspect-sample.json"),
            args => inspectArguments = [.. args]);
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        await tools.InspectAsync(severity: InspectSeverity.Warning, cancellationToken: Ct);

        // Assert
        inspectArguments.ShouldNotBeNull();
        inspectArguments.ShouldContain("--severity=WARNING");
    }

    [Fact]
    public async Task InspectAsync_ErrorSeverity_MapsToErrorCliToken()
    {
        // Arrange
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        List<string>? inspectArguments = null;
        StubJb(
            Fixtures.ReadSarif("inspect-sample.json"),
            args => inspectArguments = [.. args]);
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act — a non-default value proves the parameter, not the default, drives the CLI token.
        await tools.InspectAsync(severity: InspectSeverity.Error, cancellationToken: Ct);

        // Assert
        inspectArguments.ShouldNotBeNull();
        inspectArguments.ShouldContain("--severity=ERROR");
    }

    [Fact]
    public async Task CleanupAsync_ValidFiles_ReturnsFullSummaryClassifyingEachFile()
    {
        // Arrange — the jb stub returns exit 0 without touching the files, so both hash identically before
        // and after: a small batch renders at DetailLevel.Full, classifying each entry.
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        PlantFile(environment, "src/A.cs");
        PlantFile(environment, "src/B.cs");
        StubJb();
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        string result = await tools.CleanupAsync(["src/A.cs", "src/B.cs"], cancellationToken: Ct);

        // Assert
        result.ShouldBe(
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 0 of 2 file(s) changed on disk:\n"
            + "  - src/A.cs (unchanged)\n"
            + "  - src/B.cs (unchanged)");
    }

    [Fact]
    public async Task CleanupAsync_EntryJoiningSeveralPaths_IsSplitIntoSeparatePaths()
    {
        // Arrange — the measured caller mistake: several paths joined into one array element. It used to fail
        // the whole call as a missing file, wasting a round trip on a list every path of which is real.
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        PlantFile(environment, "src/A.cs");
        PlantFile(environment, "src/B.cs");
        List<string>? cleanupArguments = null;
        StubJb(onCleanup: args => cleanupArguments = [.. args]);
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        string result = await tools.CleanupAsync(["src/A.cs, src/B.cs"], cancellationToken: Ct);

        // Assert — jb is asked to clean both files, and both are classified and reported.
        cleanupArguments.ShouldNotBeNull();
        cleanupArguments.ShouldContain("--include=src/A.cs;src/B.cs");
        result.ShouldBe(
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 0 of 2 file(s) changed on disk:\n"
            + "  - src/A.cs (unchanged)\n"
            + "  - src/B.cs (unchanged)");
    }

    [Fact]
    public async Task CleanupAsync_AbsolutePaths_ReachJbRelativeAndAreReportedAsTheCallerSpeltThem()
    {
        // Arrange — the field failure end to end. 27 absolute paths reached jb verbatim, matched nothing, and
        // came back as exit 3 with "No items were found to cleanup"; the same list passed relative cleaned all
        // 27. The tool has always documented an absolute path as accepted, so this is that promise kept.
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        PlantFile(environment, "src/A.cs");
        PlantFile(environment, "src/B.cs");
        string[] absolute =
        [
            Path.Combine(environment.CurrentDirectory, "src", "A.cs"),
            Path.Combine(environment.CurrentDirectory, "src", "B.cs")
        ];
        List<string>? cleanupArguments = null;
        StubJb(onCleanup: args => cleanupArguments = [.. args]);
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        string result = await tools.CleanupAsync(absolute, cancellationToken: Ct);

        // Assert — translated on the way to jb, verbatim on the way back: the report answers "what you asked
        // for", which is the caller's own string.
        cleanupArguments.ShouldNotBeNull();
        cleanupArguments.ShouldContain("--include=src/A.cs;src/B.cs");
        result.ShouldBe(
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 0 of 2 file(s) changed on disk:\n"
            + $"  - {absolute[0]} (unchanged)\n"
            + $"  - {absolute[1]} (unchanged)");
    }

    [Fact]
    public async Task InspectAsync_AbsolutePaths_ReachJbRelative()
    {
        // Arrange — the same defect, and the dangerous half: jb exits 0 having matched nothing, so an
        // unmatched absolute path came back as "No issues found." with no error anywhere.
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        List<string>? inspectArguments = null;
        StubJb(
            Fixtures.ReadSarif("inspect-sample.json"),
            args => inspectArguments = [.. args]);
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        await tools.InspectAsync([Path.Combine(environment.CurrentDirectory, "src", "A.cs")], cancellationToken: Ct);

        // Assert
        inspectArguments.ShouldNotBeNull();
        inspectArguments.ShouldContain("--include=src/A.cs");
    }

    [Fact]
    public async Task CleanupAsync_JbMatchedNothing_FailsLoudlyRatherThanReadingAsANoOp()
    {
        // Arrange — a file that is on disk but in no project: jb exits 3 and says "No items were found to
        // cleanup", which an agent that has just made 27 edits reads as "nothing needed changing".
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        PlantFile(environment, "src/Orphan.cs");
        StubJbCleanupFailing(3, "No items were found to cleanup");
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => tools.CleanupAsync(["src/Orphan.cs"], cancellationToken: Ct));

        // Assert
        exception.Message.ShouldStartWith("jb cleanupcode exited with code 3. No file was cleaned up");
        exception.Message.ShouldContain("The 1 --include pattern(s) it was given:\n  - src/Orphan.cs");
    }

    [Fact]
    public async Task InspectAsync_EntryJoiningSeveralGlobs_IsSplitIntoSeparatePatterns()
    {
        // Arrange — the same mistake is worse here: the joined string reaches jb as one pattern that matches
        // nothing, and the tool reports "No issues found." for a scan that never looked at the files asked for.
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        List<string>? inspectArguments = null;
        StubJb(
            Fixtures.ReadSarif("inspect-sample.json"),
            args => inspectArguments = [.. args]);
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        await tools.InspectAsync(["src/**/*.cs,tests/**/*.cs"], cancellationToken: Ct);

        // Assert — jb's own separator is ";", so the two patterns arrive as two.
        inspectArguments.ShouldNotBeNull();
        inspectArguments.ShouldContain("--include=src/**/*.cs;tests/**/*.cs");
    }

    [Fact]
    public async Task CleanupAsync_JoinedEntryWithAMissingFragment_NamesTheFragmentAndDoesNotRunJb()
    {
        // Arrange — splitting must not blur which path is wrong: the error names the fragment that does not
        // exist, not the joined string the caller sent, and nothing is rewritten.
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        PlantFile(environment, "src/A.cs");
        StubJb();
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => tools.CleanupAsync(["src/A.cs,src/Missing.cs"], cancellationToken: Ct));

        // Assert
        exception.Message.ShouldContain("The following files were not found");
        exception.Message.ShouldContain("- src/Missing.cs");
        exception.Message.ShouldNotContain("src/A.cs,src/Missing.cs");
        await _processRunner.DidNotReceive().AnyRunWith(args => args != null && args.Count > 0 && args[0] == "cleanupcode");
    }

    [Fact]
    public async Task CleanupAsync_SolutionDeclaresProfileAndCallerOmitsOne_UsesTheDeclaredProfile()
    {
        // Arrange — the whole point of the declared profile: a caller that does not know it exists still
        // gets it. Without this, a repo that narrowed its cleanup silently gets Full Cleanup instead.
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        PlantSettingsDeclaringProfile(environment, "House: Keep Named Arguments");
        PlantFile(environment, "src/A.cs");
        StubJb();
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        string result = await tools.CleanupAsync(["src/A.cs"], cancellationToken: Ct);

        // Assert
        result.ShouldStartWith("Cleanup completed with profile \"House: Keep Named Arguments\".");
    }

    [Fact]
    public async Task CleanupAsync_CallerPassesProfile_OverridesTheDeclaredProfile()
    {
        // Arrange
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        PlantSettingsDeclaringProfile(environment, "House: Keep Named Arguments");
        PlantFile(environment, "src/A.cs");
        StubJb();
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        string result = await tools.CleanupAsync(["src/A.cs"], "Built-in: Reformat Code", cancellationToken: Ct);

        // Assert
        result.ShouldStartWith("Cleanup completed with profile \"Built-in: Reformat Code\".");
    }

    [Fact]
    public async Task CleanupAsync_BlankProfileArgument_FallsBackToTheDeclaredProfile()
    {
        // Arrange — a blank argument would reach jb as --profile= and fail the run. It has to read as
        // "unspecified" and fall through, exactly as a blank declared profile does.
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        PlantSettingsDeclaringProfile(environment, "House: Keep Named Arguments");
        PlantFile(environment, "src/A.cs");
        StubJb();
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        string result = await tools.CleanupAsync(["src/A.cs"], "   ", cancellationToken: Ct);

        // Assert
        result.ShouldStartWith("Cleanup completed with profile \"House: Keep Named Arguments\".");
    }

    [Fact]
    public async Task CleanupAsync_ProfileArgumentPaddedWithWhitespace_IsTrimmed()
    {
        // Arrange
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        PlantFile(environment, "src/A.cs");
        StubJb();
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        string result = await tools.CleanupAsync(
            ["src/A.cs"], "  Built-in: Reformat Code  ", cancellationToken: Ct);

        // Assert
        result.ShouldStartWith("Cleanup completed with profile \"Built-in: Reformat Code\".");
    }

    [Fact]
    public async Task CleanupAsync_SolutionDeclaresNoProfile_FallsBackToFullCleanup()
    {
        // Arrange
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        PlantFile(environment, "src/A.cs");
        StubJb();
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        string result = await tools.CleanupAsync(["src/A.cs"], cancellationToken: Ct);

        // Assert
        result.ShouldStartWith("Cleanup completed with profile \"Built-in: Full Cleanup\".");
    }

    [Fact]
    public async Task CleanupAsync_SettingsDeclareProfileBehindAnIllegalComment_AppliesItAndSaysNothing()
    {
        // Arrange — end to end over the field failure: `--` inside a comment is illegal XML, so this file
        // used to resolve no profile at all and silently clean up with Full Cleanup instead.
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        PlantSettings(environment, DotSettingsFixtures.DeclaringBehindIllegalComment("House: Keep Named Arguments"));
        PlantFile(environment, "src/A.cs");
        StubJb();
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        string result = await tools.CleanupAsync(["src/A.cs"], cancellationToken: Ct);

        // Assert — recovered, so the caller gets the profile and no warning about it.
        result.ShouldStartWith("Cleanup completed with profile \"House: Keep Named Arguments\".");
        result.ShouldNotContain("WARNING:");
    }

    [Fact]
    public async Task CleanupAsync_UnreadableSettings_LeadsWithAWarningBeforeTheSummary()
    {
        // Arrange — the destructive case. The files are already rewritten by the time this is rendered, and
        // they were rewritten with a broader profile than the solution declares, so the result has to say so
        // rather than leaving it in a log nobody reads.
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        PlantSettings(environment, DotSettingsFixtures.Unparseable());
        PlantFile(environment, "src/A.cs");
        StubJb();
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        string result = await tools.CleanupAsync(["src/A.cs"], cancellationToken: Ct);

        // Assert
        result.ShouldStartWith("WARNING: could not read ReSharper settings ");
        result.ShouldContain("may have used a broader profile than the solution intends.");
        result.ShouldContain("Cleanup completed with profile \"Built-in: Full Cleanup\".");
    }

    [Fact]
    public async Task InspectAsync_UnreadableSettings_SaysNothingAboutIt()
    {
        // Arrange — the blast radii differ. jb reads that file itself — the adjacent .DotSettings is a
        // layer it mounts on its own — and parses it perfectly well, so inspection severities are
        // unaffected; only this server's own profile lookup failed. Warning here would report a
        // consequence that does not exist.
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        PlantSettings(environment, DotSettingsFixtures.Unparseable());
        StubJb(Fixtures.ReadSarif("inspect-sample.json"));
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

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
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        string missing = Path.Combine(environment.CurrentDirectory, "missing.DotSettings");
        environment.SetVariable("JB_SETTINGS_PATH", missing);
        StubJb(Fixtures.ReadSarif("empty-runs.json"));
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

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
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        PlantSettings(environment, DotSettingsFixtures.SettingSeverity("MethodHasAsyncOverload", "WARNING"));
        PlantFile(environment, "Proj/Proj.csproj");
        PlantFile(environment, "Proj/Proj.csproj.DotSettings", DotSettingsFixtures.SettingSeverity("MethodHasAsyncOverload", "DO_NOT_SHOW"));
        List<string>? inspectArguments = null;
        StubJb(
            Fixtures.ReadSarif("empty-runs.json"),
            args => inspectArguments = [.. args]);
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        await tools.InspectAsync(cancellationToken: Ct);

        // Assert — no --settings: the pin that the project layer is left able to win.
        inspectArguments.ShouldNotBeNull();
        inspectArguments.Any(a => a.StartsWith("--settings", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public async Task CleanupAsync_JbSettingsPathNamesAMissingFile_LeadsWithTheSameWarning()
    {
        // Arrange — the other half of the split: this failure drops both configuration axes, so both tools
        // report it.
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        string missing = Path.Combine(environment.CurrentDirectory, "missing.DotSettings");
        environment.SetVariable("JB_SETTINGS_PATH", missing);
        PlantFile(environment, "src/A.cs");
        StubJb();
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        string result = await tools.CleanupAsync(["src/A.cs"], cancellationToken: Ct);

        // Assert
        result.ShouldStartWith($"WARNING: JB_SETTINGS_PATH is set to \"{missing}\"");
        result.ShouldContain("Cleanup completed with profile \"Built-in: Full Cleanup\".");
    }

    [Fact]
    public async Task CleanupAsync_UnreadableSettingsAndASqueezedBudget_KeepsTheWarningAndStaysWithinBudget()
    {
        // Arrange — the wiring the banner depends on: it is charged to the output budget before the body is
        // rendered, so the body reduces around it instead of the pair overflowing into the truncator. A
        // 400-token client budget is 1,000 characters, and 20 files do not list in full inside what is left.
        using FakeEnvironment environment = new();
        environment.SetVariable("MAX_MCP_OUTPUT_TOKENS", "400");
        environment.PlantSolution("App.sln");
        PlantSettings(environment, DotSettingsFixtures.Unparseable());
        string[] files = [.. Enumerable.Range(0, 20).Select(i => $"src/very/long/path/to/File{i:D3}.cs")];
        foreach (string file in files) PlantFile(environment, file);

        StubJb();
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        string result = await tools.CleanupAsync(files, cancellationToken: Ct);

        // Assert
        result.ShouldStartWith("WARNING: could not read ReSharper settings ");
        result.ShouldContain("--- DETAIL REDUCED ---"); // the body genuinely had to reduce
        result.Length.ShouldBeLessThanOrEqualTo(1_000); // banner included, so the truncator never bites
    }

    [Fact]
    public async Task InspectAsync_ResultCarriesCompilationErrors_LeadsWithTheStaleCacheNote()
    {
        // Arrange — the incident's shape reaching a real tool result: the note has to be joined onto the
        // banner inside the tool method, or it exists and nobody ever sees it.
        using FakeEnvironment environment = new();
        string cacheHome = environment.CreateTempDirectory();
        environment.SetVariable("JB_CACHE_HOME", cacheHome);
        environment.PlantSolution("App.sln");
        StubJb(Fixtures.ReadSarif("inspect-phantom-errors.json"));
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        string result = await tools.InspectAsync(cancellationToken: Ct);

        // Assert — the discriminator, the cure, and the resolved cache home the caller could not derive.
        result.ShouldStartWith("NOTE: 2 of these issue(s) are compilation errors (`.CSharpErrors`).");
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
        using FakeEnvironment environment = new();
        environment.SetVariable("MAX_MCP_OUTPUT_TOKENS", "300"); // 750 characters
        string cacheHome = environment.CreateTempDirectory();
        environment.SetVariable("JB_CACHE_HOME", cacheHome);
        environment.PlantSolution("App.sln");
        StubJb(ManyIssuesSarif(200));
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

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
        using FakeEnvironment environment = new();
        string reportRoot = environment.CreateTempDirectory();
        environment.PlantSolution("App.sln");
        StubJb(Fixtures.ReadSarif("inspect-sample.json"));
        ResharperTools tools = ToolHarness.Build(_processRunner, environment, reportRoot: reportRoot);

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
        using FakeEnvironment environment = new();
        string reportRoot = environment.CreateTempDirectory();
        environment.PlantSolution("App.sln");
        StubJb(Fixtures.ReadSarif("inspect-sample.json"));
        ResharperTools tools = ToolHarness.Build(_processRunner, environment, reportRoot: reportRoot);

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
        using FakeEnvironment environment = new();
        string reportRoot = environment.CreateTempDirectory();
        environment.PlantSolution("App.sln");
        StubJb(Fixtures.ReadSarif("empty-runs.json"));
        ResharperTools tools = ToolHarness.Build(_processRunner, environment, reportRoot: reportRoot);

        // Act
        string result = await tools.InspectAsync(report: InspectReport.Markdown, cancellationToken: Ct);

        // Assert
        result.ShouldStartWith("FULL REPORT: all 0 issue(s)");
        result.ShouldEndWith("No issues found.");
        File.ReadAllText(PathFromNote(result)).ShouldContain("No issues found.");
    }

    [Fact]
    public async Task InspectAsync_ReportMarkdownAndASqueezedBudget_KeepsTheNoteAndHoldsWhatTheResponseDropped()
    {
        // Arrange — the case the parameter exists for. The response is reduced to the one-liner while the
        // file keeps every finding, and the note naming that file has to survive the whole ladder or it
        // vanishes exactly when it matters.
        using FakeEnvironment environment = new();
        environment.SetVariable("MAX_MCP_OUTPUT_TOKENS", "300"); // 750 characters
        string reportRoot = environment.CreateTempDirectory();
        environment.PlantSolution("App.sln");
        StubJb(ManyIssuesSarif(200));
        ResharperTools tools = ToolHarness.Build(_processRunner, environment, reportRoot: reportRoot);

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
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        StubJb(Fixtures.ReadSarif("inspect-sample.json"));
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

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
        using FakeEnvironment environment = new();
        environment.SetVariable("MAX_MCP_OUTPUT_TOKENS", "300"); // 750 characters
        environment.PlantSolution("App.sln");
        StubJb(ManyIssuesSarif(200));
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

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
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        StubJb(Fixtures.ReadSarif("inspect-sample.json"));
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

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
        // the tool-facing enum and the formatting ladder to the same five spellings.
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        StubJb(Fixtures.ReadSarif("inspect-sample.json"));
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act / Assert
        foreach (InspectDetail detail in Enum.GetValues<InspectDetail>())
        {
            string result = await tools.InspectAsync(detail: detail, cancellationToken: Ct);

            // Full is the no-cap default and comes back verbatim, so there is nothing to announce.
            if (detail == InspectDetail.Full)
            {
                result.ShouldNotContain("--- DETAIL REDUCED ---");
                continue;
            }

            result.ShouldContain($"Rendered at the requested detail level {detail}");
        }
    }

    [Fact]
    public async Task InspectAsync_DetailMinimalAndReportMarkdown_AnswersInOneLineAndFilesEveryFinding()
    {
        // Arrange — the composition the pair exists for, and the survey-a-legacy-solution case in one
        // call: a cheap verdict in the response while every finding stays reachable in the file. Neither
        // parameter delivers it alone.
        using FakeEnvironment environment = new();
        string reportRoot = environment.CreateTempDirectory();
        environment.PlantSolution("App.sln");
        StubJb(ManyIssuesSarif(200));
        ResharperTools tools = ToolHarness.Build(_processRunner, environment, reportRoot: reportRoot);

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
    public async Task InspectAsync_DetailValueThatIsNoMember_FailsBeforeSpendingAJbRun()
    {
        // Arrange — unreachable through the binder, which rejects anything not a member by name. It is
        // reachable by a member added to InspectDetail and not to the mapping, and the reason the two enums
        // are mapped by hand is that such a gap must not quietly resolve to a plausible-looking level.
        using FakeEnvironment environment = new();
        environment.PlantSolution("App.sln");
        StubJb(Fixtures.ReadSarif("inspect-sample.json"));
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => tools.InspectAsync(detail: (InspectDetail)99, cancellationToken: Ct));

        // Assert — and it fails ahead of the analysis, so a bad argument never costs the minutes a run does.
        await _processRunner.DidNotReceive().AnyRunWith(args => args != null && args.Count > 0 && args[0] == "inspectcode");
    }

    [Fact]
    public async Task ResetCacheAsync_SolutionWithACachedGeneration_DropsItAndReportsTheColdNextCall()
    {
        // Arrange — end to end through the tool method, including the discovery it shares with the other two.
        using FakeEnvironment environment = new();
        string cacheHome = environment.CreateTempDirectory();
        environment.SetVariable("JB_CACHE_HOME", cacheHome);
        environment.PlantSolution("App.sln");
        string ours = CacheHomes.PlantGenerationFor(cacheHome, Path.Combine(environment.CurrentDirectory, "App.sln"));
        CacheHomes.PlantGeneration(cacheHome, "_Other.99.00");
        StubJb();
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        string result = await tools.ResetCacheAsync(cancellationToken: Ct);

        // Assert
        result.ShouldContain("Dropped 1 ReSharper cache generation(s)");
        result.ShouldContain($"  - {Path.GetFileName(ours)}");
        result.ShouldEndWith("rebuilds the cache from cold, which can take minutes.");
        Directory.Exists(ours).ShouldBeFalse();
        Directory.Exists(Path.Combine(cacheHome, "_Other.99.00")).ShouldBeTrue();
    }

    [Fact]
    public async Task ResetCacheAsync_RunsNoJbBeyondTheVersionProbe()
    {
        // Arrange — a reset is a directory delete, not an analysis. Spending a cold jb run here would double
        // the cost of the very situation the tool exists to get out of.
        using FakeEnvironment environment = new();
        string cacheHome = environment.CreateTempDirectory();
        environment.SetVariable("JB_CACHE_HOME", cacheHome);
        environment.PlantSolution("App.sln");
        StubJb();
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        await tools.ResetCacheAsync(cancellationToken: Ct);

        // Assert — the version probe (which is itself an "inspectcode" invocation) and nothing else.
        await _processRunner.Received(1).AnyRunWith(args => args != null && args.Contains("--version"));
        await _processRunner.DidNotReceive().AnyRunWith(args => args != null && !args.Contains("--version"));
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

    private static void PlantSettingsDeclaringProfile(FakeEnvironment environment, string profileName)
    {
        PlantSettings(environment, DotSettingsFixtures.Declaring(profileName));
    }

    private static void PlantSettings(FakeEnvironment environment, string content)
    {
        File.WriteAllText(Path.Combine(environment.CurrentDirectory, "App.sln.DotSettings"), content);
    }

    private static void PlantFile(FakeEnvironment environment, string relativePath, string content = "")
    {
        string fullPath = Path.Combine(environment.CurrentDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    /// <summary>
    ///     Routes the single process-runner substitute by jb sub-command: the version probe, an
    ///     inspectcode run (which writes <paramref name="inspectSarif" /> to its <c>-o=</c> path), or a
    ///     cleanupcode run. All succeed with exit code 0.
    /// </summary>
    private void StubJb(
        string inspectSarif = "",
        Action<IReadOnlyList<string>>? onInspect = null,
        Action<IReadOnlyList<string>>? onCleanup = null)
    {
        _processRunner
            .AnyRun()
            .Returns(Route);

        return;

        ProcessResult Route(CallInfo callInfo)
        {
            var arguments = callInfo.ArgAt<IReadOnlyList<string>>(1);

            if (JbStubs.IsVersionProbe(arguments)) return JbStubs.VersionProbeAnswer;

            if (arguments.Count > 0 && arguments[0] == "inspectcode")
            {
                onInspect?.Invoke(arguments);
                File.WriteAllText(OutputPathFrom(arguments), inspectSarif);
            }

            if (arguments.Count > 0 && arguments[0] == "cleanupcode") onCleanup?.Invoke(arguments);

            return new ProcessResult(0, string.Empty, string.Empty);
        }
    }

    /// <summary>
    ///     As <see cref="StubJb" />, but a <c>cleanupcode</c> run exits with <paramref name="exitCode" /> and
    ///     <paramref name="standardError" />. The version probe still succeeds, or discovery would fail before
    ///     cleanup ever ran.
    /// </summary>
    private void StubJbCleanupFailing(int exitCode, string standardError)
    {
        _processRunner
            .AnyRun()
            .Returns(callInfo =>
            {
                var arguments = callInfo.ArgAt<IReadOnlyList<string>>(1);

                return JbStubs.IsVersionProbe(arguments)
                    ? JbStubs.VersionProbeAnswer
                    : new ProcessResult(exitCode, string.Empty, standardError);
            });
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

    private static string OutputPathFrom(IReadOnlyList<string> arguments)
    {
        string arg = arguments.First(a => a.StartsWith("-o=", StringComparison.Ordinal));
        return arg["-o=".Length..];
    }
}