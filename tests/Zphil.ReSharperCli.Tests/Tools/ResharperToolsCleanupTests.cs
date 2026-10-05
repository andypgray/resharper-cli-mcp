using NSubstitute;
using NSubstitute.Core;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Tests.Tools;

/// <summary>
///     <see cref="ResharperTools.CleanupAsync" /> end to end over the two faked seams, with the config and
///     service graph real.
/// </summary>
/// <remarks>
///     The <c>files</c> validation the tool method performs itself is pinned here. An invalid enum argument is
///     validated at the binding layer instead (see <c>EnumValidationConverterFactoryTests</c> and
///     <c>CoercionIntegrationTests</c>).
/// </remarks>
public sealed class ResharperToolsCleanupTests : IDisposable
{
    private readonly FakeEnvironment _environment = new();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();

    public ResharperToolsCleanupTests()
    {
        _environment.PlantSolution("App.sln");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    ///     The <c>profile</c> argument a caller passes against a solution declaring
    ///     <c>House: Keep Named Arguments</c>, and the profile the cleanup runs with.
    /// </summary>
    public static TheoryData<string?, string> ProfileArgumentsAgainstADeclaredProfile =>
        new()
        {
            // Omitted: the whole point of the declared profile — a caller that does not know it exists still
            // gets it. Without this, a repo that narrowed its cleanup silently gets Full Cleanup instead.
            { null, "House: Keep Named Arguments" },
            // Passed: the caller's profile overrides the declared one.
            { "Built-in: Reformat Code", "Built-in: Reformat Code" },
            // Blank: it would reach jb as --profile= and fail the run, so it reads as "unspecified" and falls
            // through, exactly as a blank declared profile does.
            { "   ", "House: Keep Named Arguments" },
            // Padded with whitespace: trimmed before it is used.
            { "  Built-in: Reformat Code  ", "Built-in: Reformat Code" }
        };

    public static TheoryData<string[]?> NoFiles => new() { Array.Empty<string>(), null! };

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public async Task CleanupAsync_ValidFiles_ReturnsFullSummaryClassifyingEachFile()
    {
        // Arrange — the jb stub returns exit 0 without touching the files, so both hash identically before
        // and after: a small batch renders at DetailLevel.Full, classifying each entry.
        SolutionFiles.Plant(_environment.CurrentDirectory, "src/A.cs");
        SolutionFiles.Plant(_environment.CurrentDirectory, "src/B.cs");
        StubJb();
        ResharperTools tools = Tools();

        // Act
        string result = await tools.CleanupAsync(["src/A.cs", "src/B.cs"], cancellationToken: Ct);

        // Assert
        result.ShouldBe(
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 0 of 2 file(s) changed on disk:\n"
            + "  - src/A.cs (unchanged)\n"
            + "  - src/B.cs (unchanged)");
    }

    [Fact]
    public async Task CleanupAsync_EveryEntryAWildcard_ReportsNoRatioRatherThanZeroOfZero()
    {
        // Arrange — "0 of 0 file(s) changed on disk" reads as "nothing changed" while jb has rewritten
        // whatever the globs matched. Nothing here is measurable, so nothing here is claimed.
        SolutionFiles.Plant(_environment.CurrentDirectory, "src/A.cs");
        StubJb();
        ResharperTools tools = Tools();

        // Act
        string result = await tools.CleanupAsync(["src/**/*.cs", "tests/**/*.cs"], cancellationToken: Ct);

        // Assert — and the run is affirmed rather than merely not denied.
        result.ShouldNotContain("0 of 0");
        result.ShouldContain("Every entry was a wildcard pattern: jb cleaned what they matched");
        result.ShouldContain("  - src/**/*.cs (pattern, not hashed)");
    }

    [Fact]
    public async Task CleanupAsync_EntryJoiningSeveralPaths_IsSplitIntoSeparatePaths()
    {
        // Arrange — the measured caller mistake: several paths joined into one array element. Without the split
        // this fails the whole call as a missing file, wasting a round trip on a list every path of which is real.
        SolutionFiles.Plant(_environment.CurrentDirectory, "src/A.cs");
        SolutionFiles.Plant(_environment.CurrentDirectory, "src/B.cs");
        List<string>? cleanupArguments = null;
        StubJb(args => cleanupArguments = [.. args]);
        ResharperTools tools = Tools();

        // Act
        string result = await tools.CleanupAsync(["src/A.cs, src/B.cs"], cancellationToken: Ct);

        // Assert
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
        // Arrange — jb's --include matches relative paths only: an absolute one passed through verbatim matches
        // nothing, and the run exits 3 with "No items were found to cleanup". The tool documents an absolute
        // path as accepted, so this is that promise kept.
        SolutionFiles.Plant(_environment.CurrentDirectory, "src/A.cs");
        SolutionFiles.Plant(_environment.CurrentDirectory, "src/B.cs");
        string[] absolute =
        [
            Path.Combine(_environment.CurrentDirectory, "src", "A.cs"),
            Path.Combine(_environment.CurrentDirectory, "src", "B.cs")
        ];
        List<string>? cleanupArguments = null;
        StubJb(args => cleanupArguments = [.. args]);
        ResharperTools tools = Tools();

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
    public async Task CleanupAsync_JbMatchedNothing_FailsLoudlyRatherThanReadingAsANoOp()
    {
        // Arrange — a file that is on disk but in no project: jb exits 3 and says "No items were found to
        // cleanup", which an agent that has just edited the file reads as "nothing needed changing".
        SolutionFiles.Plant(_environment.CurrentDirectory, "src/Orphan.cs");
        StubJbCleanupFailing(3, "No items were found to cleanup");
        ResharperTools tools = Tools();

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => tools.CleanupAsync(["src/Orphan.cs"], cancellationToken: Ct));

        // Assert
        exception.Message.ShouldStartWith("jb cleanupcode exited with code 3. No file was cleaned up");
        exception.Message.ShouldContain("The 1 --include pattern(s) it was given:\n  - src/Orphan.cs");
    }

    [Fact]
    public async Task CleanupAsync_JoinedEntryWithAMissingFragment_NamesTheFragmentAndDoesNotRunJb()
    {
        // Arrange — splitting must not blur which path is wrong: the error names the fragment that does not
        // exist, not the joined string the caller sent, and nothing is rewritten.
        SolutionFiles.Plant(_environment.CurrentDirectory, "src/A.cs");
        StubJb();
        ResharperTools tools = Tools();

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => tools.CleanupAsync(["src/A.cs,src/Missing.cs"], cancellationToken: Ct));

        // Assert
        exception.Message.ShouldContain("The following files were not found");
        exception.Message.ShouldContain("- src/Missing.cs");
        exception.Message.ShouldNotContain("src/A.cs,src/Missing.cs");
        await _processRunner.DidNotReceive().AnyRunWith(args => args != null && JbStubs.IsRunOf(args, "cleanupcode"));
    }

    [Theory]
    [MemberData(nameof(ProfileArgumentsAgainstADeclaredProfile))]
    public async Task CleanupAsync_SolutionDeclaresAProfile_RunsWithTheProfileTheArgumentResolvesTo(
        string? profile,
        string expected)
    {
        // Arrange
        DotSettingsFixtures.PlantBeside(_environment.CurrentDirectory, DotSettingsFixtures.Declaring("House: Keep Named Arguments"));
        SolutionFiles.Plant(_environment.CurrentDirectory, "src/A.cs");
        StubJb();
        ResharperTools tools = Tools();

        // Act
        string result = await tools.CleanupAsync(["src/A.cs"], profile, cancellationToken: Ct);

        // Assert
        result.ShouldStartWith($"Cleanup completed with profile \"{expected}\".");
    }

    [Fact]
    public async Task CleanupAsync_SolutionDeclaresNoProfile_FallsBackToFullCleanup()
    {
        // Arrange — the end of the chain, kept out of the theory above because it plants no settings file.
        SolutionFiles.Plant(_environment.CurrentDirectory, "src/A.cs");
        StubJb();
        ResharperTools tools = Tools();

        // Act
        string result = await tools.CleanupAsync(["src/A.cs"], cancellationToken: Ct);

        // Assert
        result.ShouldStartWith("Cleanup completed with profile \"Built-in: Full Cleanup\".");
    }

    [Fact]
    public async Task CleanupAsync_SettingsDeclareProfileBehindAnIllegalComment_AppliesItAndSaysNothing()
    {
        // Arrange — end to end over the field failure: `--` inside a comment is illegal XML, so without the
        // lenient retry this file resolves no profile at all and silently cleans up with Full Cleanup instead.
        DotSettingsFixtures.PlantBeside(_environment.CurrentDirectory, DotSettingsFixtures.DeclaringBehindIllegalComment("House: Keep Named Arguments"));
        SolutionFiles.Plant(_environment.CurrentDirectory, "src/A.cs");
        StubJb();
        ResharperTools tools = Tools();

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
        DotSettingsFixtures.PlantBeside(_environment.CurrentDirectory, DotSettingsFixtures.Unparseable());
        SolutionFiles.Plant(_environment.CurrentDirectory, "src/A.cs");
        StubJb();
        ResharperTools tools = Tools();

        // Act
        string result = await tools.CleanupAsync(["src/A.cs"], cancellationToken: Ct);

        // Assert
        result.ShouldStartWith("WARNING: could not read ReSharper settings ");
        result.ShouldContain("may have used a broader profile than the solution intends.");
        result.ShouldContain("Cleanup completed with profile \"Built-in: Full Cleanup\".");
    }

    [Fact]
    public async Task CleanupAsync_JbSettingsPathNamesAMissingFile_LeadsWithTheSameWarning()
    {
        // Arrange — unlike an unreadable settings file, which costs only the cleanup profile, this failure drops
        // both configuration axes, so both tools report it.
        string missing = Path.Combine(_environment.CurrentDirectory, "missing.DotSettings");
        _environment.SetVariable("JB_SETTINGS_PATH", missing);
        SolutionFiles.Plant(_environment.CurrentDirectory, "src/A.cs");
        StubJb();
        ResharperTools tools = Tools();

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
        _environment.SetVariable("MAX_MCP_OUTPUT_TOKENS", "400");
        DotSettingsFixtures.PlantBeside(_environment.CurrentDirectory, DotSettingsFixtures.Unparseable());
        string[] files = [.. Enumerable.Range(0, 20).Select(i => $"src/very/long/path/to/File{i:D3}.cs")];
        foreach (string file in files) SolutionFiles.Plant(_environment.CurrentDirectory, file);

        StubJb();
        ResharperTools tools = Tools();

        // Act
        string result = await tools.CleanupAsync(files, cancellationToken: Ct);

        // Assert
        result.ShouldStartWith("WARNING: could not read ReSharper settings ");
        result.ShouldContain("--- DETAIL REDUCED ---"); // the body genuinely had to reduce
        result.Length.ShouldBeLessThanOrEqualTo(1_000); // banner included, so the truncator never bites
    }

    [Theory]
    [MemberData(nameof(NoFiles))]
    public async Task CleanupAsync_NoFiles_ThrowsUserErrorAndDoesNotProbeJb(string[]? files)
    {
        // Arrange
        ResharperTools tools = Tools();

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => tools.CleanupAsync(files!, cancellationToken: Ct));

        // Assert
        exception.Message.ShouldBe("At least one file must be specified.");
        await _processRunner.DidNotReceive().AnyRun();
    }

    [Fact]
    public async Task CleanupAsync_BlankFileEntry_ThrowsUserErrorNamingThePositionAndDoesNotProbeJb()
    {
        // Arrange — a blank entry names no file and would throw out of path resolution as an internal
        // error. This tool rewrites what it is given, so the whole list is rejected rather than partly run.
        ResharperTools tools = Tools();

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => tools.CleanupAsync(["src/A.cs", "  "], cancellationToken: Ct));

        // Assert
        exception.Message.ShouldBe("File paths must not be blank (files[1] is empty).");
        await _processRunner.DidNotReceive().AnyRun();
    }

    /// <summary>The tool methods over this class's environment and process runner.</summary>
    private ResharperTools Tools()
    {
        return ToolHarness.Build(_processRunner, _environment);
    }

    /// <summary>
    ///     Routes the single process-runner substitute by jb sub-command: the version probe, or a
    ///     <c>cleanupcode</c> run, handed to <paramref name="onCleanup" />. Every run succeeds the way a
    ///     healthy one does, leaving its cache generation behind.
    /// </summary>
    private void StubJb(Action<IReadOnlyList<string>>? onCleanup = null)
    {
        _processRunner
            .AnyRun()
            .Returns(Route);

        return;

        ProcessResult Route(CallInfo callInfo)
        {
            IReadOnlyList<string> arguments = callInfo.Arguments();

            if (JbStubs.IsVersionProbe(arguments)) return JbStubs.VersionProbeAnswer;

            if (JbStubs.IsRunOf(arguments, "cleanupcode")) onCleanup?.Invoke(arguments);

            return JbStubs.Succeed(arguments);
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
                IReadOnlyList<string> arguments = callInfo.Arguments();

                return JbStubs.IsVersionProbe(arguments)
                    ? JbStubs.VersionProbeAnswer
                    : new ProcessResult(exitCode, string.Empty, standardError);
            });
    }
}