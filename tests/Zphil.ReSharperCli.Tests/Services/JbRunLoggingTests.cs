using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Services;

/// <summary>
///     The pair of <c>Information</c> lines a <c>jb</c> run costs the log, and what they have to carry.
/// </summary>
/// <remarks>
///     <para>
///         This is the record a week of field logs could not produce. A 552-second <c>resharper_inspect</c>
///         appeared there with no way to say whether it had been cold, seeded, or queued behind another
///         session, because the only timing in the file came from the MCP SDK and said nothing about the
///         cache. Both halves of the pair are pinned: the opening line, whose value is that it exists
///         <em>before</em> minutes of silence and carries the two facts that predict them, and the closing
///         one, which says how it actually ended.
///     </para>
///     <para>
///         Every ending is a line, including the two that are not a clean exit. A run killed at the cap and a
///         speculative pass stood down both leave the process without an exit code, and a log that recorded
///         only clean exits would show those runs starting and never finishing — which is exactly the shape
///         the pre-warm's own logging had, and the reason it was unreadable.
///     </para>
/// </remarks>
public sealed class JbRunLoggingTests : IDisposable
{
    private readonly string _cacheHome;
    private readonly FakeEnvironment _environment = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly string _solutionPath;

    public JbRunLoggingTests()
    {
        _cacheHome = _environment.CreateTempDirectory();
        _solutionPath = _environment.CreateSolutionPath("App.sln");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ResolvedConfig Config => Configs.Bare(_solutionPath, _cacheHome);

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public async Task RunAsync_CleanExit_WritesTheStartingAndExitLinesAtInformation()
    {
        // Arrange
        StubExit(0);

        // Act
        await Runner().RunAsync(Config, ["inspectcode", _solutionPath], Ct);

        // Assert — the opening line names the run and how long it queued for the generation...
        LogEntry starting = StartingLine();
        starting.Level.ShouldBe(LogLevel.Information);
        starting.Property("Subcommand").ShouldBe("inspectcode");
        starting.Property("SolutionPath").ShouldBe(_solutionPath);
        starting.Property("RunKind").ShouldBe("for a call");
        starting.Property("QueueWaitMs").ShouldNotBeNull();

        // ...and the closing line the outcome and the wall clock, which is the whole diagnosis of a slow call.
        LogEntry exited = ExitLine();
        exited.Level.ShouldBe(LogLevel.Information);
        exited.Property("Subcommand").ShouldBe("inspectcode");
        exited.Property("ExitCode").ShouldBe(0);
        exited.Property("ElapsedMs").ShouldNotBeNull();
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_StillReportsTheCodeRatherThanOnlyThrowing()
    {
        // Arrange — the caller is told through an exception, and the log has to keep the fact independently:
        // a UserErrorException is deliberately never logged, so without this the failure leaves no trace.
        StubExit(3);

        // Act
        await Should.ThrowAsync<UserErrorException>(() => Runner().RunAsync(Config, ["cleanupcode", _solutionPath], Ct));

        // Assert
        ExitLine().Property("ExitCode").ShouldBe(3);
    }

    [Fact]
    public async Task RunAsync_NoGenerationOnDisk_ReadsTheCacheAsCold()
    {
        // Arrange — the first run against a fresh checkout, which is the case that runs for minutes.
        StubExit(0);

        // Act
        await Runner().RunAsync(Config, ["inspectcode", _solutionPath], Ct);

        // Assert
        StartingLine().Property("CacheState").ShouldBe("cold (none on disk)");
    }

    [Fact]
    public async Task RunAsync_GenerationWithAWarmMarker_ReadsTheCacheAsWarmAndNamesTheGeneration()
    {
        // Arrange
        string generation = CacheHomes.PlantWarmDonor(_cacheHome, _solutionPath);
        StubExit(0);

        // Act
        await Runner().RunAsync(Config, ["inspectcode", _solutionPath], Ct);

        // Assert — warm, with the marker's age and the directory jb is about to open.
        var state = StartingLine().Property("CacheState").ShouldBeOfType<string>();
        state.ShouldStartWith("warm (");
        state.ShouldContain(Path.GetFileName(generation));
    }

    [Fact]
    public async Task RunAsync_CacheJustSeededFromASibling_SaysSoRatherThanCallingItPartBuilt()
    {
        // Arrange — the state a fresh worktree beside an analysed checkout starts in, and the one the log most
        // has to be able to name: a seeded run's duration sits between a warm one's and a cold one's, so a
        // reader who cannot see the seeding cannot account for it.
        CacheHomes.PlantWarmDonor(_cacheHome, _environment.CreateSolutionPath("App.sln"));
        StubExit(0);

        // Act
        await Runner().RunAsync(Config, ["inspectcode", _solutionPath], Ct);

        // Assert — read from disk alone this is indistinguishable from the killed-run remnant below, because a
        // seed deliberately stamps no marker. Only the transplant knows, so only it can say.
        StartingLine().Property("CacheState").ShouldBeOfType<string>().ShouldStartWith("seeded from a sibling checkout (");
    }

    [Fact]
    public async Task RunAsync_GenerationWarmedByAnotherJbBuild_ReadsTheCacheAsStale()
    {
        // Arrange — the whole plumbing in one run: the marker this generation carries names the build that
        // warmed it, the config names the build about to open it, and the two disagree. Everything on disk
        // says warm, and jb is about to rebuild it in place — 220 seconds against the 64 its own second run
        // took, measured across one patch bump.
        CacheHomes.PlantWarmDonor(_cacheHome, _solutionPath, "2026.2.0.2");
        StubExit(0);

        // Act
        await Runner().RunAsync(Configs.Bare(_solutionPath, _cacheHome, "2026.2.1"), ["inspectcode", _solutionPath], Ct);

        // Assert
        StartingLine().Property("CacheState")
            .ShouldBe("stale (cache written by jb 2026.2.0.2, this is 2026.2.1, and jb rebuilds it)");
    }

    [Fact]
    public async Task RunAsync_GenerationWithNoMarker_ReadsTheCacheAsPartBuilt()
    {
        // Arrange — the remnant of a run that was killed. Neither warm nor quite cold, and the state that
        // explains a run taking almost as long as a cold one on a cache home that looks populated.
        CacheHomes.PlantGenerationFor(_cacheHome, _solutionPath);
        StubExit(0);

        // Act
        await Runner().RunAsync(Config, ["inspectcode", _solutionPath], Ct);

        // Assert
        StartingLine().Property("CacheState").ShouldBeOfType<string>().ShouldStartWith("part-built (");
    }

    [Fact]
    public async Task RunAsync_AfterAReset_ReadsTheCacheAsColdOnPurpose()
    {
        // Arrange — a reset is why the next call is slow, and the tombstone is the only record of it that
        // outlives the process that wrote it.
        JbColdTombstone.Write(_solutionPath, _cacheHome, NullLogger.Instance);
        StubExit(0);

        // Act
        await Runner().RunAsync(Config, ["inspectcode", _solutionPath], Ct);

        // Assert
        StartingLine().Property("CacheState").ShouldBe("cold after a reset (none on disk)");
    }

    [Fact]
    public async Task RunAsync_SecondColdRunOfASolution_OpensNamingWhatTheFirstOneCost()
    {
        // Arrange — the pair of runs the feature exists for. Neither leaves a generation behind, so both read
        // the cache as cold and the second is genuinely comparable to the first.
        StubExit(0);

        // Act
        await Runner().RunAsync(Config, ["inspectcode", _solutionPath], Ct);
        await Runner().RunAsync(Config, ["inspectcode", _solutionPath], Ct);

        // Assert — the first run has nothing to quote and reads exactly as it did before any of this existed;
        // the second carries the figure, keyed by the band. A stubbed run finishes in microseconds, which
        // rounds and clamps to one second on both sides of the record.
        IReadOnlyList<LogEntry> opened = _logs.WithProperty("CacheState");
        opened.Count.ShouldBe(2);
        opened[0].Property("CacheState").ShouldBe("cold (none on disk)");
        opened[1].Property("CacheState").ShouldBe("cold (none on disk; the last cold run took 1 second)");
    }

    [Fact]
    public async Task RunAsync_KilledAtTheCap_SaysSoRatherThanEndingWithNoLine()
    {
        // Arrange
        _processRunner
            .AnyRunOf("jb")
            .Returns<ProcessResult>(_ => throw new ProcessTimeoutException("'jb' timed out."));

        // Act
        await Should.ThrowAsync<UserErrorException>(() => Runner().RunAsync(Config, ["inspectcode", _solutionPath], Ct));

        // Assert — no exit code to report, so the cap is reported instead, at the same level.
        LogEntry killed = _logs.WithProperty("RunCap").ShouldHaveSingleItem();
        killed.Level.ShouldBe(LogLevel.Information);
        killed.Property("Subcommand").ShouldBe("inspectcode");
        killed.Property("ElapsedMs").ShouldNotBeNull();
        _logs.WithProperty("ExitCode").ShouldBeEmpty();
    }

    [Fact]
    public async Task TryRunAsync_SpeculativePass_LabelsItselfSoItsLinesAreNotReadAsACall()
    {
        // Arrange — a pre-warm's run lines and a call's interleave in one file, and the reader has to be able
        // to tell whose minutes these were.
        StubExit(0);

        // Act
        await Runner().TryRunAsync(Config, ["inspectcode", _solutionPath], Ct);

        // Assert
        StartingLine().Property("RunKind").ShouldBe("speculative");
    }

    /// <summary>The opening line, identified by the property only it carries.</summary>
    private LogEntry StartingLine()
    {
        return _logs.WithProperty("CacheState").ShouldHaveSingleItem();
    }

    private LogEntry ExitLine()
    {
        return _logs.WithProperty("ExitCode").ShouldHaveSingleItem();
    }

    private JbRunner Runner()
    {
        return JbRunners.Create(_processRunner, logs: Logs.Capturing(_logs));
    }

    private void StubExit(int exitCode)
    {
        _processRunner
            .AnyRunOf("jb")
            .Returns(new ProcessResult(exitCode, string.Empty, string.Empty));
    }
}