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

    private const string FixtureDirectoryName = "ContractSolution";
    private const string SolutionFileName = "ContractFixture.slnx";
    private const string MisformattedFileName = "Misformatted.cs";

    /// <summary>A file that genuinely does not compile, planted only in the copy that exists to provoke one.</summary>
    private const string BrokenFileName = "Broken.cs";

    private const string BrokenFileContent = """
                                             namespace ContractFixture;

                                             internal class Broken
                                             {
                                                 public int Value => NoSuchType.NoSuchMember;
                                             }

                                             """;

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

    private readonly FakeEnvironment _environment = new();

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

    /// <summary>The solution the runs above analysed, and the cache home they filled.</summary>
    internal string SolutionPath { get; private set; } = "";

    internal string CacheHome { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        if (!IsInstalled) return;

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // The machine's real home directory, so JbLocator's ~/.dotnet/tools/jb candidate resolves for a
        // client that did not inherit PATH. Reading the real environment is allowed here; it is writing it
        // that the parallel run cannot survive.
        _environment.HomeDirectory = new SystemEnvironment().HomeDirectory;
        CacheHome = _environment.CreateTempDirectory();
        _environment.SetVariable("JB_CACHE_HOME", CacheHome);

        ProcessRunner processRunner = new(NullLogger<ProcessRunner>.Instance);

        SolutionPath = PlantSolution("solution");
        await BuildAsync(processRunner, SolutionPath, cancellationToken);

        // The second copy is deliberately never built: it carries a file that does not compile, which is
        // how the compilation-error rule id gets provoked at all.
        string brokenSolutionPath = PlantSolution("broken");
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetDirectoryName(brokenSolutionPath)!, BrokenFileName),
            BrokenFileContent,
            cancellationToken);

        _environment.SetVariable("JB_SOLUTION_PATH", SolutionPath);

        JbLocator locator = new(processRunner, _environment, NullLogger<JbLocator>.Instance);
        ConfigResolver configResolver = new(locator, _environment, NullLogger<ConfigResolver>.Instance);
        JbRunner jbRunner = JbRunners.Create(processRunner);
        InspectService inspectService = new(jbRunner);
        CleanupService cleanupService = new(jbRunner, NullLogger<CleanupService>.Instance);

        Installation = await locator.LocateAsync(cancellationToken);
        Config = await configResolver.ResolveAsync(null, cancellationToken);

        // Suggestion rather than the default Warning: the report has to carry both tiers for the severity
        // token to be worth checking at all.
        Issues = await inspectService.RunAsync(Config, null, InspectSeverity.Suggestion, cancellationToken);

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
        _environment.Dispose();

        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Copy the fixture solution into a directory of its own, and return the copied solution file's path.
    /// </summary>
    private string PlantSolution(string name)
    {
        string destination = Path.Combine(_environment.CreateTempDirectory(), name);
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
        _environment.SetVariable("JB_CACHE_HOME", _environment.CreateTempDirectory());
        try
        {
            return await configResolver.ResolveAsync(brokenSolutionPath, cancellationToken);
        }
        finally
        {
            _environment.SetVariable("JB_CACHE_HOME", CacheHome);
        }
    }

    /// <summary>
    ///     Restore the misformatted file, then run a real cleanup over it. The restore is what makes a pass
    ///     detectable more than once: cleanup is idempotent, so a second pass over an already-formatted file
    ///     rewrites nothing and the check would pass vacuously.
    /// </summary>
    private async Task<CleanupRun> CleanUpAsync(
        CleanupService cleanupService,
        IReadOnlyList<string> files,
        string? profile,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(Config.SolutionDirectory, MisformattedFileName);
        File.Copy(Path.Combine(FixtureDirectory, MisformattedFileName), path, true);
        string before = await File.ReadAllTextAsync(path, cancellationToken);

        CleanupOutcome outcome = await cleanupService.RunAsync(Config, files, profile, cancellationToken);
        string after = await File.ReadAllTextAsync(path, cancellationToken);

        return new CleanupRun(outcome, before, after);
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
        string outputFile = Path.Combine(_environment.CreateTempDirectory(), "results.json");
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

    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", FixtureDirectoryName);

    private static void ReplaceIncludeWith(List<string> arguments, string absolutePath)
    {
        int index = arguments.FindIndex(argument => argument.StartsWith("--include=", StringComparison.Ordinal));
        arguments[index] = $"--include={absolutePath}";
    }

    /// <summary>The <c>jb</c> the gate found, and the version banner it printed.</summary>
    private static JbPresence LocateJb()
    {
        SystemEnvironment environment = new();
        ProcessRunner runner = new(NullLogger<ProcessRunner>.Instance);

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
}

/// <summary>
///     One real cleanup pass: what the product reported, and what the file on disk said either side of it.
///     Both, because the product's own <c>Changed</c> classification is itself a hash comparison, and a
///     check that read only that would be proving the classifier against itself.
/// </summary>
internal sealed record CleanupRun(CleanupOutcome Outcome, string TextBefore, string TextAfter)
{
    public bool FileWasRewritten => !string.Equals(TextBefore, TextAfter, StringComparison.Ordinal);
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
