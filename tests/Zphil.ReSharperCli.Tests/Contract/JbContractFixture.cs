using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Infrastructure;
using Zphil.ReSharperCli.Sarif;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Contract;

/// <summary>
///     Everything <see cref="JbContractTests" /> asserts over, produced once by a handful of real <c>jb</c>
///     runs against a real copy of the fixture solution. The runs live here rather than in the test methods
///     because each costs tens of seconds and several of them answer more than one contract.
/// </summary>
/// <remarks>
///     <para>
///         The fixture solution is copied to a temp directory and built there, never analysed in the repo
///         tree: cleanup rewrites the files it is given, and <c>obj/project.assets.json</c> records absolute
///         paths, so copying the built output would point back at the working tree.
///     </para>
///     <para>
///         <see cref="InitializeAsync" /> does nothing at all when <c>jb</c> is absent. The skip gate on the
///         test methods reports them as skipped either way, but a class fixture is built before those gates
///         decide anything, so the fixture has to be the thing that costs nothing on a machine — or a release
///         runner — with no <c>jb</c> installed.
///     </para>
/// </remarks>
public sealed class JbContractFixture : IAsyncLifetime
{
    /// <summary>
    ///     The <c>jb</c> major line (<c>YYYY.N</c>) these contracts were last read against by hand. JetBrains
    ///     ships roughly two major lines and thirty patch releases a year; the patches have to pass in silence
    ///     or the signal drowns, so only a change to this line is reported. Nothing else in the repo records
    ///     which <c>jb</c> its assumptions were verified against.
    /// </summary>
    internal const string LastVerifiedMajorLine = "2026.2";

    /// <summary>
    ///     Where the soft-tier report is written, set by the scheduled workflow. Unset — which is every local
    ///     run — the report reaches the test output and nothing else.
    /// </summary>
    internal const string ReportVariable = "JB_CONTRACT_REPORT";

    /// <summary>The one fixture file inspection finds nothing in.</summary>
    /// <remarks>
    ///     Every other file is bait, so without it no run could show what a report says about a file that was
    ///     inspected and came out clean.
    /// </remarks>
    internal const string CleanFileName = "Clean.cs";

    /// <summary>The file cleanup is run over: committed misformatted, and restored before every pass.</summary>
    internal const string MisformattedFileName = "Misformatted.cs";

    /// <summary>The cleanup profile the fixture solution's <c>.DotSettings</c> declares.</summary>
    internal const string DeclaredProfile = "Built-in: Reformat Code";

    private const string FixtureDirectoryName = "ContractSolution";
    private const string SolutionFileName = "ContractFixture.slnx";

    /// <summary>A file that genuinely does not compile, planted only in the copy that exists to provoke one.</summary>
    private const string BrokenFileName = "Broken.cs";

    private const string BrokenFileContent = """
                                             namespace ContractFixture;

                                             internal class Broken
                                             {
                                                 public int Value => NoSuchType.NoSuchMember;
                                             }

                                             """;

    /// <summary>How much of jb's output a cleanup pass keeps as evidence, from the end.</summary>
    private const int StandardOutputTailLines = 60;

    private const int StandardErrorTailLines = 20;

    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     The <c>jb</c> this machine has, probed once per process. Deliberately not
    ///     <see cref="JbLocator.LocateAsync" /> — the gate has to be synchronous (<c>SkipUnless</c> reads a
    ///     bool property), and it keeps the raw version banner the tests assert on — but it enumerates
    ///     <see cref="JbLocator.Candidates" /> and spawns <see cref="JbLocator.ProbeArguments" />, so a
    ///     candidate added to the product is a candidate this gate finds. What <see cref="JbLocator" /> makes
    ///     of the same candidates is a contract in its own right, asserted rather than assumed.
    /// </summary>
    private static readonly Lazy<JbPresence> Presence = new(LocateJb);

    /// <summary>Filled as the runs below go past, keyed by subcommand.</summary>
    private readonly Dictionary<string, ProgressVocabulary> _progressVocabularies = new(StringComparer.Ordinal);

    /// <summary>
    ///     What the server logged while the runs below were made, kept so a cleanup pass can quote its own share.
    /// </summary>
    private readonly CapturingLoggerProvider _serverLog = new();

    /// <summary>
    ///     Created only once <see cref="InitializeAsync" /> has decided there is a <c>jb</c> to run, so the
    ///     fixture still costs nothing on a machine without one.
    /// </summary>
    private ChildProcessLifetime? _childLifetime;

    /// <summary>
    ///     The environment seam, created only once <see cref="InitializeAsync" /> has decided there is a
    ///     <c>jb</c> to run: constructing one creates two temp directories, and the fixture has to cost nothing
    ///     on a machine without <c>jb</c>.
    /// </summary>
    private FakeEnvironment? _environment;

    /// <summary>The process seam every run goes through, kept so a cleanup pass can read back what jb printed.</summary>
    private LineRecordingRunner? _runs;

    /// <summary>Whether a <c>jb</c> was found, and so whether any of the members below were ever filled in.</summary>
    public static bool IsInstalled => Presence.Value.ExecutablePath is not null;

    /// <summary>
    ///     The banner <c>jb inspectcode --version</c> printed, captured by the gate probe. Read only from
    ///     tests the gate has already let through, so the throw below is unreachable — and is a throw rather
    ///     than a defaulted result because a defaulted one reads as "exit code 0, no output", which is a
    ///     failure this suite would report as a jb that stopped printing its version.
    /// </summary>
    internal static ProcessResult VersionProbe =>
        Presence.Value.Probe ?? throw new InvalidOperationException("No jb was found, so no version was captured.");

    /// <summary>What <see cref="JbLocator" /> made of the same machine — the product's own discovery path.</summary>
    internal JbInstallation Installation { get; private set; } = null!;

    /// <summary>The configuration a real call resolves for the fixture solution, warnings and all.</summary>
    internal ResolvedConfig Config { get; private set; } = null!;

    /// <summary>What a solution-wide <c>resharper_inspect</c> reports, at the widest severity.</summary>
    internal IReadOnlyList<InspectIssue> Issues { get; private set; } = [];

    /// <summary>The same, over a copy carrying a genuine compilation error.</summary>
    internal IReadOnlyList<InspectIssue> BrokenSolutionIssues { get; private set; } = [];

    /// <summary>Its resolved configuration, whose cache home the compilation-error note has to name.</summary>
    internal ResolvedConfig BrokenSolutionConfig { get; private set; } = null!;

    /// <summary>A cleanup pass with the built-in profile literal, over a relative path.</summary>
    internal CleanupRun BuiltInProfileCleanup { get; private set; } = null!;

    /// <summary>A cleanup pass with no profile argument, over an absolute one.</summary>
    internal CleanupRun DeclaredProfileCleanup { get; private set; } = null!;

    /// <summary>How each subcommand answered an <c>--include</c> that was left absolute.</summary>
    internal RawIncludeProbe RawAbsoluteInclude { get; private set; } = null!;

    /// <summary>What the solution-wide inspect's report lists under <c>run.artifacts</c>.</summary>
    internal SarifArtifactsProbe SarifArtifacts { get; private set; } = null!;

    /// <summary>
    ///     What <see cref="JbProgressLines" /> made of the standard output of the real runs above, per
    ///     subcommand. The progress heartbeat is driven entirely off that classification, and it is reading
    ///     someone else's undocumented output — so it is watched here rather than assumed.
    /// </summary>
    internal IReadOnlyDictionary<string, ProgressVocabulary> ProgressVocabularies => _progressVocabularies;

    /// <summary>The solution the runs above analysed, and the cache home they filled.</summary>
    internal string SolutionPath { get; private set; } = "";

    internal string CacheHome { get; private set; } = "";

    private static string FixtureDirectory => Fixtures.PathTo(FixtureDirectoryName);

    /// <summary>
    ///     <see cref="_environment" />, for the steps <see cref="InitializeAsync" /> reaches only after creating
    ///     it.
    /// </summary>
    private FakeEnvironment SeamEnvironment =>
        _environment ?? throw new InvalidOperationException("No jb was found, so no environment was created.");

    public async ValueTask InitializeAsync()
    {
        if (!IsInstalled) return;

        _environment = new FakeEnvironment();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // The machine's real home directory, so JbLocator's ~/.dotnet/tools/jb candidate resolves for a
        // client that did not inherit PATH. Reading the real environment is allowed here; it is writing it
        // that the parallel run cannot survive.
        SeamEnvironment.HomeDirectory = new SystemEnvironment().HomeDirectory;
        CacheHome = SeamEnvironment.CreateTempDirectory();
        SeamEnvironment.SetVariable("JB_CACHE_HOME", CacheHome);

        ILoggerFactory serverLog = Logs.Capturing(_serverLog);

        _childLifetime = new ChildProcessLifetime(new SystemEnvironment(), NullLogger<ChildProcessLifetime>.Instance);
        ProcessRunner realProcessRunner = new(_childLifetime, serverLog.CreateLogger<ProcessRunner>());

        // Every run below goes through the recorder, so the runs that answer the other contracts answer the
        // progress-vocabulary one for free — and answer it from the same bytes jb actually wrote, split by
        // the product's own reader rather than by a re-spelling of it.
        _runs = new LineRecordingRunner(realProcessRunner, RecordProgressLine);
        IProcessRunner processRunner = _runs;

        SolutionPath = PlantSolution("solution");
        await BuildAsync(processRunner, SolutionPath, cancellationToken);

        // The second copy is deliberately never built: it carries a file that does not compile, which is
        // how the compilation-error rule id gets provoked at all.
        string brokenSolutionPath = PlantSolution("broken");
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetDirectoryName(brokenSolutionPath)!, BrokenFileName),
            BrokenFileContent,
            cancellationToken);

        SeamEnvironment.SetVariable("JB_SOLUTION_PATH", SolutionPath);

        JbLocator locator = new(processRunner, SeamEnvironment, NullLogger<JbLocator>.Instance);
        ConfigResolver configResolver = new(locator, SeamEnvironment, NullLogger<ConfigResolver>.Instance);
        JbRunner jbRunner = JbRunners.Create(processRunner, logs: serverLog);
        InspectService inspectService = new(jbRunner);
        CleanupService cleanupService = new(jbRunner, serverLog.CreateLogger<CleanupService>());

        Installation = await locator.LocateAsync(cancellationToken);
        Config = await configResolver.ResolveAsync(null, cancellationToken);

        // Suggestion rather than the default Warning: the report has to carry both tiers for the severity
        // token to be worth checking at all.
        Issues = await inspectService.RunAsync(Config, null, InspectSeverity.Suggestion, cancellationToken);
        SarifArtifacts = ObserveSarifArtifacts(_runs.LastReport, _runs.LastResult);

        BuiltInProfileCleanup = await CleanUpAsync(
            cleanupService, [MisformattedFileName], CleanupService.DefaultProfile, cancellationToken);

        // Absolute, and with no profile argument: one pass answers the declared-profile contract and the
        // absolute-path translation at once, because CleanupService does both on the way to the same run.
        string absoluteMisformatted = Path.Combine(Config.SolutionDirectory, MisformattedFileName);
        DeclaredProfileCleanup = await CleanUpAsync(cleanupService, [absoluteMisformatted], null, cancellationToken);

        RawAbsoluteInclude = await ProbeRawAbsoluteIncludeAsync(jbRunner, absoluteMisformatted, cancellationToken);

        BrokenSolutionConfig = await ResolveBrokenSolutionAsync(configResolver, brokenSolutionPath, cancellationToken);
        BrokenSolutionIssues = await inspectService.RunAsync(
            BrokenSolutionConfig, null, InspectSeverity.Suggestion, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        // Every run this fixture made has finished by now, so closing the job kills nothing it still needed.
        _childLifetime?.Dispose();
        _environment?.Dispose();

        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Copy the fixture solution into a directory of its own, and return the copied solution file's path.
    /// </summary>
    private string PlantSolution(string name)
    {
        string destination = Path.Combine(SeamEnvironment.CreateTempDirectory(), name);
        CopyDirectory(FixtureDirectory, destination);

        return Path.Combine(destination, SolutionFileName);
    }

    /// <summary>
    ///     Build the copy. A fixture that does not compile would make every inspection result meaningless, so
    ///     this is the one step here allowed to fail the whole suite loudly.
    /// </summary>
    private static async Task BuildAsync(
        IProcessRunner processRunner, string solutionPath, CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner.RunAsync(
            "dotnet", ["build", solutionPath, "--nologo", "-v", "quiet"], BuildTimeout, cancellationToken);

        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Building the contract fixture solution failed with exit code {result.ExitCode}.\n"
                + $"{result.StandardOutput}\n{result.StandardError}");
    }

    /// <summary>
    ///     Resolve the broken copy against a cache home of its own. The two copies share a solution
    ///     <em>file name</em>, which is exactly what makes one a transplant donor for the other — and a run
    ///     seeded from a sibling is not the cold run the compilation-error check means to be making.
    /// </summary>
    private async Task<ResolvedConfig> ResolveBrokenSolutionAsync(
        ConfigResolver configResolver, string brokenSolutionPath, CancellationToken cancellationToken)
    {
        SeamEnvironment.SetVariable("JB_CACHE_HOME", SeamEnvironment.CreateTempDirectory());
        try
        {
            return await configResolver.ResolveAsync(brokenSolutionPath, cancellationToken);
        }
        finally
        {
            SeamEnvironment.SetVariable("JB_CACHE_HOME", CacheHome);
        }
    }

    /// <summary>
    ///     Restores the misformatted file, then runs a real cleanup over it, reading the file, the cache
    ///     generations and the server's log on either side.
    /// </summary>
    /// <remarks>
    ///     The restore is what makes a pass detectable more than once: cleanup is idempotent, so a second pass
    ///     over an already-formatted file rewrites nothing and the check would pass vacuously.
    /// </remarks>
    private async Task<CleanupRun> CleanUpAsync(
        CleanupService cleanupService,
        IReadOnlyList<string> files,
        string? profile,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(Config.SolutionDirectory, MisformattedFileName);
        File.Copy(Path.Combine(FixtureDirectory, MisformattedFileName), path, true);

        FileFacts restored = FileFacts.Read(path);
        string generationsBefore = Safely(GenerationNames);
        int firstLogEntry = _serverLog.Entries.Count;

        CleanupOutcome outcome = await cleanupService.RunAsync(Config, files, profile, cancellationToken);

        FileFacts afterPass = FileFacts.Read(path);
        ProcessResult? jb = _runs?.LastResult;

        StringBuilder details = new();
        details.Append($"generations before: {generationsBefore}\n");
        details.Append($"generations after:  {Safely(GenerationNames)}\n");
        details.Append($"solution directory: {Safely(SolutionDirectoryListing)}\n");
        details.Append($"classified:         {Classification(outcome)}\n");
        details.Append($"jb stdout, last {StandardOutputTailLines} lines:\n{Tail(jb?.StandardOutput, StandardOutputTailLines)}");
        details.Append($"jb stderr, last {StandardErrorTailLines} lines:\n{Tail(jb?.StandardError, StandardErrorTailLines)}");
        details.Append($"server log during the pass:\n{ServerLogSince(firstLogEntry)}");

        CleanupEvidence evidence = new(restored, afterPass, Printed(jb, MisformattedFileName), details.ToString());

        return new CleanupRun(outcome, evidence);
    }

    /// <summary>This solution's cache generations under the run's cache home, by directory name.</summary>
    private string GenerationNames()
    {
        JbSolutionGenerations generations = JbCacheGenerations.FindFor(Config.CacheHome, Config.SolutionPath);
        IEnumerable<string> owned = generations.Owned.Select(generation => generation.Name);
        IEnumerable<string> neighbours = generations.Neighbours.Select(generation => $"{generation.Name} (neighbour)");
        List<string> names = [.. owned, .. neighbours];

        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    /// <summary>Everything beside the cleanup target, so a save that left a temporary file behind shows.</summary>
    private string SolutionDirectoryListing()
    {
        IEnumerable<string> entries = new DirectoryInfo(Config.SolutionDirectory)
            .EnumerateFileSystemInfos()
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => entry is FileInfo file ? $"{file.Name} ({file.Length})" : $"{entry.Name}/");

        return string.Join(", ", entries);
    }

    private static string Classification(CleanupOutcome outcome)
    {
        return string.Join(", ", outcome.Entries.Select(entry => $"{entry.Display} {entry.Status}"));
    }

    /// <summary>What the server logged from entry <paramref name="first" /> on, one indented line each.</summary>
    private string ServerLogSince(int first)
    {
        IEnumerable<string> lines = _serverLog.Entries
            .Skip(first)
            .Select(entry => $"  [{entry.Level}] {entry.Category[(entry.Category.LastIndexOf('.') + 1)..]}: {entry.Message}\n");

        return string.Concat(lines);
    }

    /// <summary>
    ///     Whether a line <c>jb</c> printed names <paramref name="fileName" /> after <paramref name="prefix" />, or
    ///     <see langword="null" /> when no run was captured.
    /// </summary>
    private static bool? Printed(ProcessResult? jb, string fileName, string prefix = "")
    {
        return jb?.StandardOutput
            .Split('\n')
            .Select(line => line.Trim())
            .Any(line => line.StartsWith(prefix, StringComparison.Ordinal) && NamesFile(line[prefix.Length..], fileName));
    }

    /// <summary>
    ///     Whether <paramref name="path" /> ends in the file <paramref name="fileName" />: a path, a line of
    ///     <c>jb</c>'s output naming one, or a SARIF <c>file://</c> URI, whose last segment is the file name all
    ///     the same.
    /// </summary>
    private static bool NamesFile(string path, string fileName)
    {
        // Case-blind: these observations ask whether jb names the file, not how it cases it, and a soft-tier
        // check that read a casing change as drift would report a finding that is not one.
        return string.Equals(Path.GetFileName(path.Trim()), fileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The last <paramref name="count" /> non-blank lines of <paramref name="text" />, indented.</summary>
    private static string Tail(string? text, int count)
    {
        if (text is null) return "  (no run captured)\n";

        List<string> lines = text
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Trim().Length > 0)
            .ToList();

        return lines.Count == 0 ? "  (none)\n" : string.Concat(lines.TakeLast(count).Select(line => $"  {line}\n"));
    }

    /// <summary>What <paramref name="read" /> produces, or why it could not, as <see cref="DescribeFailure" /> puts it.</summary>
    private static string Safely(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception exception)
        {
            return $"unavailable: {DescribeFailure(exception)}";
        }
    }

    /// <summary>A reading that failed, as one line of evidence.</summary>
    /// <remarks>
    ///     Evidence is gathered inside <see cref="InitializeAsync" />, where one exception would fail every test in
    ///     the class, so every reading that can fail is caught and described with this instead. Line breaks are
    ///     flattened so a message cannot break the one-fact-per-line layout of the report and the failure message.
    /// </remarks>
    internal static string DescribeFailure(Exception exception)
    {
        return $"{exception.GetType().Name}: {exception.Message.ReplaceLineEndings(" ")}";
    }

    /// <summary>
    ///     Run both subcommands with the <c>--include</c> left absolute. This is the one place the suite
    ///     spells a <c>jb</c> argument itself, because <em>not</em> applying
    ///     <see cref="FilePathList.ToIncludePattern" /> is the whole point of the probe — and even here only
    ///     the include entry is swapped, so a change to how a run is configured still reaches it.
    /// </summary>
    private async Task<RawIncludeProbe> ProbeRawAbsoluteIncludeAsync(
        JbRunner jbRunner, string absolutePath, CancellationToken cancellationToken)
    {
        List<string> cleanupArguments = CleanupService.BuildArguments(
            Config, [MisformattedFileName], CleanupService.DefaultProfile);
        ReplaceIncludeWith(cleanupArguments, absolutePath);

        int cleanupExitCode = await ExitCodeOfAsync(jbRunner, cleanupArguments, cancellationToken);

        // The environment's temp-directory lifecycle, like every other scratch this fixture makes: the
        // few-KB SARIF lives until DisposeAsync deletes it with the rest.
        string outputFile = Path.Combine(SeamEnvironment.CreateTempDirectory(), "results.json");
        List<string> inspectArguments = InspectService.BuildArguments(
            Config, outputFile, [MisformattedFileName], InspectSeverity.Suggestion);
        ReplaceIncludeWith(inspectArguments, absolutePath);

        int inspectExitCode = await ExitCodeOfAsync(jbRunner, inspectArguments, cancellationToken);

        // No output file at all is one of the two refusals this has been seen to make, so its absence is
        // read as "reported nothing" rather than as a failure.
        var issues = 0;
        if (File.Exists(outputFile))
        {
            await using FileStream sarif = File.OpenRead(outputFile);
            issues = (await SarifParser.ParseAsync(sarif, cancellationToken)).Count;
        }

        return new RawIncludeProbe(cleanupExitCode, new RawIncludeInspect(inspectExitCode, issues));
    }

    /// <summary>
    ///     The exit code of one run, whichever way it ended. <see cref="JbRunner" /> raises a non-zero exit
    ///     as an exception because every real caller treats it as a failure; here the code itself is the
    ///     observation.
    /// </summary>
    private async Task<int> ExitCodeOfAsync(
        JbRunner jbRunner, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult result = await jbRunner.RunAsync(Config, arguments, cancellationToken);

            return result.ExitCode;
        }
        catch (JbExitCodeException exception)
        {
            return exception.ExitCode;
        }
    }

    private static void ReplaceIncludeWith(List<string> arguments, string absolutePath)
    {
        int index = arguments.FindIndex(argument => argument.StartsWith("--include=", StringComparison.Ordinal));
        arguments[index] = $"--include={absolutePath}";
    }

    /// <summary>
    ///     Sets what the solution-wide inspect's report lists under <c>run.artifacts</c> beside what jb printed
    ///     about <see cref="CleanFileName" /> and what <see cref="SarifParser" /> made of the same report.
    /// </summary>
    /// <remarks>
    ///     The array is read as raw JSON because no product code reads it: reading it is the observation.
    /// </remarks>
    private SarifArtifactsProbe ObserveSarifArtifacts(string? report, ProcessResult? jb)
    {
        int filesWithFindings = Issues.Select(issue => issue.File).Distinct(StringComparer.Ordinal).Count();
        int cleanFileFindings = Issues.Count(issue => NamesFile(issue.File, CleanFileName));
        bool cleanFileInspected = Printed(jb, CleanFileName, JbProgressLines.InspectingFilePrefix) == true;

        // What is known without the report. Each ending below adds what the report said, or why it said nothing.
        SarifArtifactsProbe probe = new(null, null, filesWithFindings, cleanFileInspected, cleanFileFindings, null);

        if (report is null) return probe with { Unreadable = "no report was captured from the run" };

        // A report shaped other than expected is recorded as the reason it could not be read.
        try
        {
            using JsonDocument document = JsonDocument.Parse(report);
            List<string?>? uris = ArtifactUris(document.RootElement);

            return probe with
            {
                ArtifactCount = uris?.Count,
                CleanFileListed = uris is not null && uris.Any(uri => uri is not null && NamesFile(uri, CleanFileName))
            };
        }
        catch (Exception exception)
        {
            return probe with { Unreadable = DescribeFailure(exception) };
        }
    }

    /// <summary>
    ///     The location URI of every entry in every run's <c>artifacts</c> array, null for an entry that names
    ///     none, or <see langword="null" /> when no run carries the array at all.
    /// </summary>
    private static List<string?>? ArtifactUris(JsonElement report)
    {
        List<string?>? uris = null;

        foreach (JsonElement run in report.GetProperty("runs").EnumerateArray())
        {
            if (!run.TryGetProperty("artifacts", out JsonElement artifacts)) continue;

            uris ??= [];
            foreach (JsonElement artifact in artifacts.EnumerateArray())
                uris.Add(
                    artifact.TryGetProperty("location", out JsonElement location)
                    && location.TryGetProperty("uri", out JsonElement uri)
                        ? uri.GetString()
                        : null);
        }

        return uris;
    }

    /// <summary>
    ///     File one line of a real run's standard output under the subcommand that produced it, and record
    ///     what <see cref="JbProgressLines" /> made of it.
    /// </summary>
    private void RecordProgressLine(string subcommand, string line)
    {
        lock (_progressVocabularies)
        {
            if (!_progressVocabularies.TryGetValue(subcommand, out ProgressVocabulary? vocabulary))
                _progressVocabularies[subcommand] = vocabulary = new ProgressVocabulary();

            vocabulary.Add(line);
        }
    }

    /// <summary>The <c>jb</c> the gate found, and the version banner it printed.</summary>
    private static JbPresence LocateJb()
    {
        SystemEnvironment environment = new();

        // Scoped to the gate: the two probes below are the only thing it spawns, and both have ended by the
        // time this is disposed.
        using ChildProcessLifetime childLifetime = new(environment, NullLogger<ChildProcessLifetime>.Instance);
        ProcessRunner runner = new(childLifetime, NullLogger<ProcessRunner>.Instance);

        foreach (string candidate in JbLocator.Candidates(environment.HomeDirectory))
            try
            {
                // Blocking on purpose: SkipUnless reads a bool property, so the gate cannot be async, and
                // xUnit installs no synchronization context for this to deadlock against.
                ProcessResult result = runner
                    .RunAsync(candidate, JbLocator.ProbeArguments, JbLocator.ProbeTimeout, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                if (result.ExitCode == 0) return new JbPresence(candidate, result);
            }
            catch (Exception)
            {
                // A missing executable (Win32Exception) or a probe that ran out of time — try the next.
            }

        return new JbPresence(null, null);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);

        foreach (string directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    /// <summary>A <c>jb</c> executable the gate probe accepted, with the banner it printed.</summary>
    private sealed record JbPresence(string? ExecutablePath, ProcessResult? Probe);

    /// <summary>
    ///     Tees every line of a run's standard output to <paramref name="record" /> on its way to whatever
    ///     the product asked for, keyed by the subcommand that produced it, and keeps the report an inspect run
    ///     wrote.
    /// </summary>
    /// <remarks>
    ///     A decorator rather than a progress sink threaded through the services, because the two see
    ///     different things: a progress sink receives this server's own rendering, on a heartbeat, which says
    ///     nothing about the words <c>jb</c> used. The observation this suite exists to make is about <c>jb</c>'s
    ///     vocabulary, so it has to be made where the vocabulary is. The report is kept here for the same reason:
    ///     <see cref="InspectService" /> deletes it as soon as it has parsed it, so the only window in which its raw
    ///     text exists is between <c>jb</c>'s exit and this method's return.
    /// </remarks>
    private sealed class LineRecordingRunner(IProcessRunner inner, Action<string, string> record) : IProcessRunner
    {
        /// <summary>What the most recent run returned, or <see langword="null" /> when it threw.</summary>
        /// <remarks>
        ///     The fixture runs one thing at a time, so a caller that reads this as soon as its run returns reads
        ///     its own result.
        /// </remarks>
        public ProcessResult? LastResult { get; private set; }

        /// <summary>
        ///     The report the most recent run wrote, or <see langword="null" /> when it named no output file,
        ///     wrote none, or the file could not be read.
        /// </summary>
        public string? LastReport { get; private set; }

        public async Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            Action<string>? onOutputLine = null)
        {
            string subcommand = arguments.Count > 0 ? arguments[0] : "";
            LastResult = null;
            LastReport = null;

            ProcessResult result = await inner.RunAsync(fileName, arguments, timeout, cancellationToken, line =>
            {
                record(subcommand, line);
                onOutputLine?.Invoke(line);
            });

            LastResult = result;
            LastReport = await ReadReportAsync(arguments, cancellationToken);

            return result;
        }

        private static async Task<string?> ReadReportAsync(
            IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            if (JbStubs.OutputPathOf(arguments) is not { } path) return null;

            try
            {
                return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Inside the run the product is waiting on: a throw here would fail the inspect it observes.
                return null;
            }
        }
    }
}

/// <summary>
///     What one subcommand's real standard output looked like to <see cref="JbProgressLines" />: how much of
///     it was recognised, and which phases it announced.
/// </summary>
internal sealed class ProgressVocabulary
{
    private readonly HashSet<JbRunPhase> _phases = [];

    /// <summary>Every non-blank line the run wrote.</summary>
    public int Lines { get; private set; }

    /// <summary>Of those, the ones this server understood.</summary>
    public int Recognised { get; private set; }

    /// <summary>Of those, the per-file lines a heartbeat's count is made of.</summary>
    public int FileLines { get; private set; }

    /// <summary>The phases the run announced.</summary>
    public IReadOnlySet<JbRunPhase> Phases => _phases;

    public void Add(string line)
    {
        if (line.Trim().Length == 0) return;

        Lines++;

        if (JbProgressLines.Classify(line) is not { } step) return;

        Recognised++;
        _phases.Add(step.Phase);

        if (step.NamesAFile) FileLines++;
    }
}

/// <summary>
///     One real cleanup pass: what the product reported, and the fixture's account of the pass, which reads the
///     file's text on either side of it. The text as well as the product's report, because the product's own
///     <c>Changed</c> classification is itself a hash comparison, and a check that read only that would be
///     proving the classifier against itself.
/// </summary>
internal sealed record CleanupRun(CleanupOutcome Outcome, CleanupEvidence Evidence)
{
    /// <summary>What each part of <see cref="Signature" /> says, in its order, for whatever labels the row.</summary>
    internal const string SignatureColumns = "rewritten / named by jb / write time moved";

    /// <summary>
    ///     Whether the file's text changed across the pass, or <see langword="null" /> when either reading of it
    ///     failed.
    /// </summary>
    public bool? FileWasRewritten =>
        Evidence.Restored.Text is { } restored && Evidence.AfterPass.Text is { } afterPass
            ? !string.Equals(restored, afterPass, StringComparison.Ordinal)
            : null;

    /// <summary>The pass as one row of the soft report, in <see cref="SignatureColumns" /> order.</summary>
    public string Signature =>
        $"{SoftReport.YesNo(FileWasRewritten)} / {SoftReport.YesNo(Evidence.JbNamedTheFile)} / "
        + SoftReport.YesNo(Evidence.WriteTimeMoved);

    /// <summary>Everything the fixture knows about the pass, as a failure message carries it.</summary>
    public string Describe()
    {
        return $"The \"{Outcome.Profile}\" pass ({SignatureColumns}: {Signature})\n"
               + $"restored:           {Evidence.Restored.Description}\n"
               + $"after the pass:     {Evidence.AfterPass.Description}\n"
               + Evidence.Details;
    }
}

/// <summary>The words the soft report's observation rows share.</summary>
internal static class SoftReport
{
    /// <summary>An observation that was made either way, or <c>unknown</c> when it could not be made.</summary>
    public static string YesNo(bool? value)
    {
        return value switch
        {
            true => "yes",
            false => "no",
            null => "unknown"
        };
    }
}

/// <summary>How each subcommand answered an <c>--include</c> that was left absolute.</summary>
internal sealed record RawIncludeProbe(int CleanupExitCode, RawIncludeInspect Inspect);

/// <summary>
///     What <c>inspectcode</c> did with that argument. The two endings it has been seen to have are both
///     refusals — exit 0 with an empty report through 2026.1, and a non-zero exit writing no report file at
///     all in 2026.2 — so the probe records the exit code and the issue count rather than pinning either
///     shape.
/// </summary>
internal sealed record RawIncludeInspect(int ExitCode, int IssueCount);

/// <summary>
///     What a solution-wide inspect's report lists under SARIF's <c>run.artifacts</c>, against
///     <see cref="JbContractFixture.CleanFileName" />, a file the same run inspected and found nothing in. The
///     array is the one part of a report that could vouch for such a file, and so the only signal that could
///     tell a <c>files</c> entry that matched nothing from a file that came out clean.
/// </summary>
/// <remarks>
///     Measured on <c>jb</c> 2026.2.3.1: the array holds one entry per file with a finding and leaves the clean
///     file out, so it lists only files the results already name and carries nothing they do not. That is why
///     <see cref="SarifParser" /> leaves it unread. A report that lists the clean file is the change that would
///     make the array worth parsing, and the soft tier reports it as drift.
/// </remarks>
/// <param name="Unreadable">Why the report could not be read, or <see langword="null" /> when it was.</param>
/// <param name="ArtifactCount">
///     How many entries the <c>artifacts</c> arrays hold, or <see langword="null" /> when the report carries
///     none.
/// </param>
/// <param name="FilesWithFindings">How many distinct files the report's results name.</param>
/// <param name="CleanFileInspected">Whether jb's own output named the clean file as one it inspected.</param>
/// <param name="CleanFileFindings">How many findings the clean file drew.</param>
/// <param name="CleanFileListed">
///     Whether <c>artifacts</c> lists the clean file, or <see langword="null" /> when the report could not be
///     read.
/// </param>
internal sealed record SarifArtifactsProbe(
    string? Unreadable,
    int? ArtifactCount,
    int FilesWithFindings,
    bool CleanFileInspected,
    int CleanFileFindings,
    bool? CleanFileListed)
{
    /// <summary>What each part of <see cref="Signature" /> says, in its order, for whatever labels the row.</summary>
    internal const string SignatureColumns =
        "listed / files with findings; " + JbContractFixture.CleanFileName + " inspected / findings / listed";

    /// <summary>
    ///     The probe as one row of the soft report, in <see cref="SignatureColumns" /> order. Whether the clean
    ///     file is listed answers something only when it was inspected and drew nothing.
    /// </summary>
    public string Signature
    {
        get
        {
            string listed = Unreadable is not null ? "unreadable"
                : ArtifactCount is { } count ? $"{count}"
                : "no array";

            return $"{listed} / {FilesWithFindings}; {SoftReport.YesNo(CleanFileInspected)} / {CleanFileFindings} / "
                   + SoftReport.YesNo(CleanFileListed);
        }
    }
}