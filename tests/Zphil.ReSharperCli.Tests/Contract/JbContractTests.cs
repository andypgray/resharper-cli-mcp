using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Infrastructure;
using Zphil.ReSharperCli.Sarif;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestSupport;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Tests.Contract;

/// <summary>
///     What this server assumes about <c>jb</c>, checked against a real one. Every other test in the suite
///     runs against a recorded SARIF fixture or an NSubstitute double, so JetBrains could change any of these
///     and the build would stay green — and they ship roughly thirty stable releases a year.
/// </summary>
/// <remarks>
///     <para>
///         Two tiers. The methods below turn the run red: they are the behaviours that stop the server
///         working at all. The single soft-tier method reports rather than asserts, because the surfaces it
///         watches are ones the code is designed to degrade safely on — a red build for those would be a
///         false alarm, and false alarms are what kill a watcher job.
///     </para>
///     <para>
///         Every check drives the product's own code rather than a re-spelling of it: a test that hardcoded
///         its own <c>jb</c> argument list would drift from <see cref="InspectService" /> and stop testing it.
///         The one exception is the absolute-<c>--include</c> probe, where not applying
///         <see cref="FilePathList.ToIncludePattern" /> is the entire point.
///     </para>
///     <para>
///         Gated behind the <c>JbContract</c> trait, so <c>ci.yml</c> keeps the every-PR suite offline, and
///         behind <see cref="JbIsInstalled" />, so the unfiltered <c>dotnet test</c> the release workflow runs
///         on a machine with no <c>jb</c> reports these as skipped rather than failing the release.
///     </para>
/// </remarks>
[Trait("Category", "JbContract")]
public sealed class JbContractTests(JbContractFixture fixture, ITestOutputHelper output)
    : IClassFixture<JbContractFixture>
{
    private const string NoJb =
        "Requires the JetBrains ReSharper command-line tools: dotnet tool install -g JetBrains.ReSharper.GlobalTools";

    private const string VersionPrefix = "Version:";

    /// <summary>
    ///     The severity labels <see cref="SarifParser.MapSeverity" /> knows how to produce — derived from
    ///     <see cref="InspectSeverity" /> rather than re-spelled, so a tier added to the product does not
    ///     reach this watcher as false "unmapped level" drift.
    /// </summary>
    private static readonly string[] MappedSeverities =
        Enum.GetValues<InspectSeverity>().Select(severity => severity.ToString().ToUpperInvariant()).ToArray();

    /// <summary>Read by <c>SkipUnless</c> on every method below.</summary>
    public static bool JbIsInstalled => JbContractFixture.IsInstalled;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Skip = NoJb, SkipUnless = nameof(JbIsInstalled))]
    public void VersionProbe_PrintsTheVersionLineJbLocatorParses()
    {
        // Act
        ProcessResult probe = JbContractFixture.VersionProbe;
        List<string> versions = probe.StandardOutput
            .Split('\n')
            .Where(line => line.StartsWith(VersionPrefix, StringComparison.Ordinal))
            .Select(line => line[VersionPrefix.Length..].Trim())
            .ToList();

        // Assert — JbLocator reads the version off a line starting "Version:", falling back to the whole
        // trimmed output. The fallback would swallow a banner reshuffle silently, reporting a paragraph as a
        // version number, so the line itself is what gets pinned.
        probe.ExitCode.ShouldBe(0);
        versions.ShouldNotBeEmpty();
        versions.ShouldAllBe(version => version.Length > 0);
    }

    [Fact(Skip = NoJb, SkipUnless = nameof(JbIsInstalled))]
    public void JbLocator_ResolvesTheVersionTheProbePrinted()
    {
        // Act
        JbInstallation installation = fixture.Installation;

        // Assert — the discovery path a real call takes lands on the same jb, and reads the same version out
        // of it. An empty version here is how a candidate that "worked" but said nothing would look.
        installation.ExecutablePath.ShouldNotBeNullOrWhiteSpace();
        installation.Version.ShouldNotBeNullOrWhiteSpace();
        JbContractFixture.VersionProbe.StandardOutput.ShouldContain(installation.Version);
    }

    [Fact(Skip = NoJb, SkipUnless = nameof(JbIsInstalled))]
    public void InspectArguments_AreAcceptedInFull()
    {
        // Assert — InspectService.RunAsync raises a non-zero exit through JbRunner and a missing output file
        // itself, so a report that parsed at all is every flag in the list being accepted: -o, --severity,
        // --swea, --no-build, --absolute-paths and --caches-home together.
        fixture.Issues.ShouldNotBeEmpty();

        // And the settings axis that decides whether --settings rides the command line at all: the fixture
        // declares a solution-level .DotSettings, which jb mounts itself, so the flag must stay off.
        fixture.Config.SettingsPath.ShouldNotBeNull();
        fixture.Config.SettingsPathIsCustomLayer.ShouldBeFalse();
        fixture.Config.Warnings.ShouldBe(ConfigWarnings.None);
    }

    [Fact(Skip = NoJb, SkipUnless = nameof(JbIsInstalled))]
    public void Sarif_YieldsIssuesTheFormatterCanPointAt()
    {
        // Act
        IReadOnlyList<InspectIssue> issues = fixture.Issues;

        // Assert — the shape every consumer downstream of SarifParser assumes: a rule id to name, a file that
        // is rooted (the --absolute-paths contract) and still on disk, and a line to jump to. A URI scheme
        // change or a switch to relative paths would land here rather than as an agent opening nothing.
        issues.ShouldNotBeEmpty();
        issues.ShouldAllBe(issue => issue.RuleId.Length > 0);
        issues.ShouldAllBe(issue => Path.IsPathRooted(issue.File));
        issues.ShouldAllBe(issue => File.Exists(issue.File));
        issues.ShouldContain(issue => issue.Line > 0);
    }

    [Fact(Skip = NoJb, SkipUnless = nameof(JbIsInstalled))]
    public void SeverityToken_WidensTheReportRatherThanSelectingOneTier()
    {
        // Act
        int suggestions = CountOf("SUGGESTION");
        int warnings = CountOf("WARNING");

        // Assert — --severity=SUGGESTION is a floor, not an exact match: warnings have to come back with the
        // suggestions. If it ever became an exact filter, `severity: "Suggestion"` would silently stop
        // reporting the tier callers care most about.
        suggestions.ShouldBeGreaterThan(0);
        warnings.ShouldBeGreaterThan(0);
        suggestions.ShouldBeGreaterThanOrEqualTo(warnings);
    }

    [Fact(Skip = NoJb, SkipUnless = nameof(JbIsInstalled))]
    public void CleanupWithTheBuiltInProfile_RewritesTheFile()
    {
        // Act
        CleanupRun run = fixture.BuiltInProfileCleanup;

        // Assert — the profile literal this server falls back to is still one jb knows. An unknown profile is
        // a non-zero exit, which CleanupService restates as a failed pass, so this failing red is the whole
        // default path being gone.
        run.Outcome.Profile.ShouldBe(CleanupService.DefaultProfile);
        run.FileWasRewritten.ShouldBeTrue();
        run.Outcome.Entries.ShouldAllBe(entry => entry.Status == CleanupFileStatus.Changed);
    }

    [Fact(Skip = NoJb, SkipUnless = nameof(JbIsInstalled))]
    public void CleanupWithTheSolutionsDeclaredProfile_RewritesTheFile()
    {
        // Assert — the profile chain end to end: CleanupProfileReader lifts SilentCleanupProfile out of the
        // fixture's .DotSettings, ConfigResolver carries it, CleanupService passes it as --profile, and jb
        // accepts it. jb never reads that key itself, so this is the only thing making a repo's declared
        // narrowing apply to a call that named no profile.
        fixture.Config.CleanupProfile.ShouldBe("Built-in: Reformat Code");
        fixture.DeclaredProfileCleanup.Outcome.Profile.ShouldBe("Built-in: Reformat Code");
        fixture.DeclaredProfileCleanup.FileWasRewritten.ShouldBeTrue();
    }

    [Fact(Skip = NoJb, SkipUnless = nameof(JbIsInstalled))]
    public void IncludePattern_TranslatedFromAnAbsolutePath_Matches()
    {
        // Arrange — the entry the declared-profile pass was given, which is the form an agent tends to send.
        string absolute = Path.Combine(fixture.Config.SolutionDirectory, "Misformatted.cs");

        // Act
        string pattern = FilePathList.ToIncludePattern(absolute, fixture.Config.SolutionDirectory);

        // Assert — the translation still produces the spelling jb matches against the solution model, and the
        // file it named was genuinely rewritten. Its own report echoes the caller's absolute path back
        // untranslated, which is the half a unit test cannot check.
        pattern.ShouldBe("Misformatted.cs");
        fixture.DeclaredProfileCleanup.Outcome.Entries.Single().Display.ShouldBe(absolute);
        fixture.DeclaredProfileCleanup.FileWasRewritten.ShouldBeTrue();
    }

    [Fact(Skip = NoJb, SkipUnless = nameof(JbIsInstalled))]
    public async Task ToolsCall_Inspect_ReturnsIssuesNamingAFixtureFile()
    {
        // Arrange — the production DI graph over the real process seam, so this is a genuine jb run reaching a
        // real MCP client through the coercion layer, the global filter, and the reduction ladder.
        await using McpPipelineHarness harness = await McpPipelineHarness.StartAsync(
            Ct,
            arrange: (environment, _) =>
            {
                environment.HomeDirectory = new SystemEnvironment().HomeDirectory;
                environment.SetVariable("JB_SOLUTION_PATH", fixture.SolutionPath);
                environment.SetVariable("JB_CACHE_HOME", fixture.CacheHome);
            },
            processRunner: new ProcessRunner(NullLogger<ProcessRunner>.Instance));

        // Act
        CallToolResult result = await harness.Client.CallToolAsync(
            ResharperTools.InspectToolName, cancellationToken: Ct);

        // Assert — a client sees issues, not an error, and the rendered markdown names a file from the
        // fixture. Everything the other checks assert one layer at a time has to hold at once for this to.
        result.IsError.ShouldNotBe(true);
        string text = result.Content.OfType<TextContentBlock>().First().Text;
        text.ShouldContain("Unused.cs");
        harness.Logs.Warnings.ShouldBeEmpty();
    }

    /// <summary>
    ///     The soft tier: the surfaces the server degrades safely on. Nothing here fails the run — the report
    ///     goes to the test output and, under the scheduled workflow, to the file it turns into
    ///     <c>::warning::</c> annotations and a job summary.
    /// </summary>
    [Fact(Skip = NoJb, SkipUnless = nameof(JbIsInstalled))]
    public async Task SoftContracts_AreReportedRatherThanAsserted()
    {
        // The one hard assertion, and the reason it is here: without it a broken fixture would report an
        // empty finding list, which reads exactly like a clean bill of health.
        fixture.Issues.ShouldNotBeEmpty();

        List<string> findings = [];

        // Hash drift costs a skipped optimisation, never a wrong delete: a computed hash matching no
        // directory means "nothing here is provably ours". Cache reset then drops nothing and transplanting
        // stops seeding, both silently — which is why it is watched at all.
        string generation = Path.Combine(
            fixture.CacheHome, JbSolutionCacheHash.FirstGenerationDirectoryName(fixture.SolutionPath));

        if (!Directory.Exists(generation))
            findings.Add(
                $"Cache generation naming has drifted: nothing at \"{Path.GetFileName(generation)}\" under the "
                + "run's cache home. resharper_reset_cache will drop nothing and CacheTransplanter will stop "
                + "seeding, both without an error.");

        // The leading dot on the rule id is jb's own and nothing documents it. CompilationErrorNote already
        // matches the undotted spelling too, so a rename here is cosmetic rather than a break.
        if (!fixture.BrokenSolutionIssues.Any(issue => issue.RuleId == CompilationErrorNote.RuleId))
            findings.Add(
                $"A solution with a genuine compilation error reported no `{CompilationErrorNote.RuleId}` "
                + $"issue. Rule ids seen: {RuleIdsOf(fixture.BrokenSolutionIssues)}.");

        if (CompilationErrorNote.For(fixture.BrokenSolutionIssues, fixture.BrokenSolutionConfig.CacheHome).Length == 0)
            findings.Add(
                "CompilationErrorNote did not fire on a solution that does not compile, so an agent meeting "
                + "phantom errors would not be told how to tell them from real ones.");

        // The premise behind ToIncludePattern. jb has refused an absolute --include two different ways —
        // exit 0 with an empty report through 2026.1, a non-zero exit writing no report at all in 2026.2 —
        // so what is watched is that it still refuses, not how.
        RawIncludeProbe raw = fixture.RawAbsoluteInclude;
        if (raw.CleanupExitCode == 0)
            findings.Add(
                "jb cleanupcode now accepts an absolute --include (exit 0). The translation in "
                + "FilePathList.ToIncludePattern may no longer be needed.");

        if (raw.Inspect is { ExitCode: 0, IssueCount: > 0 })
            findings.Add(
                $"jb inspectcode now matches an absolute --include ({raw.Inspect.IssueCount} issue(s) at exit 0). "
                + "The translation in FilePathList.ToIncludePattern may no longer be needed.");

        string majorLine = MajorLineOf(fixture.Installation.Version);
        if (majorLine != JbContractFixture.LastVerifiedMajorLine)
            findings.Add(
                $"jb has moved to major line {majorLine}; these contracts were last read by hand against "
                + $"{JbContractFixture.LastVerifiedMajorLine}. Re-read them and move LastVerifiedMajorLine.");

        // An unmapped level passes through verbatim and upper-cased, so it would reach an agent as a severity
        // label nothing in the formatter knows — sortable, but not one of the three tiers documented anywhere.
        var unmapped = fixture.Issues
            .Select(issue => issue.Severity)
            .Where(severity => !MappedSeverities.Contains(severity))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (unmapped.Count > 0)
            findings.Add(
                $"SARIF carried level(s) SarifParser.MapSeverity does not map: {string.Join(", ", unmapped)}. "
                + "They reach the client as-is.");

        string report = BuildReport(findings);
        output.WriteLine(report);

        // The scheduled workflow sets this and turns the file into a job summary plus one warning annotation
        // per "- " line. Reading env is fine here; the no-env-mutation rule bars only writes.
        string? reportPath = Environment.GetEnvironmentVariable(JbContractFixture.ReportVariable);
        if (!string.IsNullOrWhiteSpace(reportPath)) await File.WriteAllTextAsync(reportPath, report, Ct);
    }

    /// <summary>
    ///     The report the workflow publishes: what was observed, then the drift. Only the finding lines start
    ///     with <c>- </c>, which is what the workflow turns into annotations, so the observations can stay in
    ///     the summary of a run with nothing wrong with it. Explicit <c>'\n'</c> keeps the file
    ///     byte-identical across OSes, so the workflow's line-by-line bash parse is deterministic.
    /// </summary>
    private string BuildReport(IReadOnlyList<string> findings)
    {
        RawIncludeProbe raw = fixture.RawAbsoluteInclude;

        StringBuilder builder = new();
        builder.Append("## jb contract report\n\n");
        builder.Append("| Observation | Value |\n|---|---|\n");
        builder.Append($"| jb version | {fixture.Installation.Version} |\n");
        builder.Append($"| Last hand-verified major line | {JbContractFixture.LastVerifiedMajorLine} |\n");
        builder.Append($"| Issues reported solution-wide at SUGGESTION | {fixture.Issues.Count} |\n");
        builder.Append($"| Of those, WARNING | {CountOf("WARNING")} |\n");
        builder.Append($"| Compilation-error issues on the broken copy | {BrokenCompilationErrors()} |\n");
        builder.Append($"| Absolute --include, cleanupcode exit | {raw.CleanupExitCode} |\n");
        builder.Append(
            $"| Absolute --include, inspectcode exit / issues | {raw.Inspect.ExitCode} / {raw.Inspect.IssueCount} |\n\n");

        if (findings.Count == 0)
        {
            builder.Append("No drift: every soft contract held.\n");

            return builder.ToString();
        }

        builder.Append($"{findings.Count} soft contract(s) drifted. These degrade safely rather than breaking ");
        builder.Append("the server, so the run stays green — but the code that assumes them is now assuming ");
        builder.Append("something this jb no longer does.\n\n");

        foreach (string finding in findings) builder.Append($"- {finding}\n");

        return builder.ToString();
    }

    private int BrokenCompilationErrors()
    {
        return fixture.BrokenSolutionIssues.Count(issue => issue.RuleId == CompilationErrorNote.RuleId);
    }

    private int CountOf(string severity)
    {
        return fixture.Issues.Count(issue => issue.Severity == severity);
    }

    private static string RuleIdsOf(IReadOnlyList<InspectIssue> issues)
    {
        IEnumerable<string> distinct = issues
            .Select(issue => issue.RuleId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        return issues.Count == 0 ? "none" : string.Join(", ", distinct);
    }

    /// <summary>
    ///     The <c>YYYY.N</c> prefix of a full <c>jb</c> version. Patch releases have to pass in silence — some
    ///     thirty a year — so only this much of the version is compared.
    /// </summary>
    private static string MajorLineOf(string version)
    {
        string[] parts = version.Split('.');

        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : version;
    }
}
