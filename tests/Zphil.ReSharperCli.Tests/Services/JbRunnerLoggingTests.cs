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
///     Pins the pair of <c>Information</c> lines a <c>jb</c> run costs the log, and what they have to carry.
/// </summary>
/// <remarks>
///     <para>
///         Without the pair, nothing says whether a slow call was cold, seeded, or queued behind another
///         session, because the only other timing in the log comes from the MCP SDK and says nothing about the
///         cache. The opening line is written before minutes of silence and carries the cache state and queue
///         wait that predict them; the closing one says how the run ended.
///     </para>
///     <para>
///         Every ending is a line. A run killed at the cap and a speculative pass stood down leave the process
///         without an exit code, and a log that recorded only clean exits would show those runs starting and
///         never finishing.
///     </para>
/// </remarks>
public sealed class JbRunnerLoggingTests : IDisposable
{
    private readonly string _cacheHome;
    private readonly FakeEnvironment _environment = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly string _solutionPath;

    public JbRunnerLoggingTests()
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
        LogEntry starting = ShouldHaveSingleStartingLine();
        starting.Level.ShouldBe(LogLevel.Information);
        starting.Property("Subcommand").ShouldBe("inspectcode");
        starting.Property("SolutionPath").ShouldBe(_solutionPath);
        starting.Property("RunKind").ShouldBe("for a call");
        starting.Property("QueueWaitMs").ShouldNotBeNull();

        // ...and the closing line the outcome and the wall clock.
        LogEntry exited = ShouldHaveSingleExitLine();
        exited.Level.ShouldBe(LogLevel.Information);
        exited.Property("Subcommand").ShouldBe("inspectcode");
        exited.Property("ExitCode").ShouldBe(0);
        exited.Property("ElapsedMs").ShouldNotBeNull();
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_StillReportsTheCodeRatherThanOnlyThrowing()
    {
        // Arrange — the caller is told through an exception, and the log has to keep the fact independently:
        // a UserErrorException is never logged, so without this the failure leaves no trace.
        StubExit(3);

        // Act
        await Should.ThrowAsync<UserErrorException>(() => Runner().RunAsync(Config, ["cleanupcode", _solutionPath], Ct));

        // Assert — at Information like a clean exit: a jb that reported failure is an outcome, not an incident.
        LogEntry exited = ShouldHaveSingleExitLine();
        exited.Level.ShouldBe(LogLevel.Information);
        exited.Property("ExitCode").ShouldBe(3);
    }

    [Fact]
    public async Task RunAsync_NoGenerationOnDisk_ReadsTheCacheAsCold()
    {
        // Arrange — the first run against a fresh checkout, which is the case that runs for minutes.
        StubExit(0);

        // Act
        await Runner().RunAsync(Config, ["inspectcode", _solutionPath], Ct);

        // Assert
        ShouldHaveSingleStartingLine().Property("CacheState").ShouldBe("cold (none on disk)");
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
        var state = ShouldHaveSingleStartingLine().Property("CacheState").ShouldBeOfType<string>();
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

        // Assert — read from disk alone this is indistinguishable from the remnant a killed run leaves, because
        // a seed stamps no marker; only a run that succeeds stamps one. Only the transplant knows, so only it
        // can say.
        ShouldHaveSingleStartingLine().Property("CacheState").ShouldBeOfType<string>().ShouldStartWith("seeded from a sibling checkout (");
    }

    [Fact]
    public async Task RunAsync_GenerationWarmedByAnotherJbBuild_ReadsTheCacheAsStale()
    {
        // Arrange — end to end in one run: the marker this generation carries names the build that warmed it,
        // the config names the build about to open it, and the two disagree. Everything on disk says warm, and
        // jb is about to rebuild it in place, at several times the cost of a warm run.
        CacheHomes.PlantWarmDonor(_cacheHome, _solutionPath, "2026.2.0.2");
        StubExit(0);

        // Act
        await Runner().RunAsync(Configs.Bare(_solutionPath, _cacheHome, "2026.2.1"), ["inspectcode", _solutionPath], Ct);

        // Assert
        ShouldHaveSingleStartingLine().Property("CacheState")
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
        ShouldHaveSingleStartingLine().Property("CacheState").ShouldBeOfType<string>().ShouldStartWith("part-built (");
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
        ShouldHaveSingleStartingLine().Property("CacheState").ShouldBe("cold after a reset (none on disk)");
    }

    [Fact]
    public async Task RunAsync_SecondColdRunOfASolution_OpensNamingWhatTheFirstOneCost()
    {
        // Arrange — the pair of runs the cost record exists for. Neither leaves a generation behind, so both
        // read the cache as cold and the second is comparable to the first.
        StubExit(0);

        // Act
        await Runner().RunAsync(Config, ["inspectcode", _solutionPath], Ct);
        await Runner().RunAsync(Config, ["inspectcode", _solutionPath], Ct);

        // Assert — the first run has nothing to quote and reads as a plain cold line; the second carries the
        // figure, keyed by the band. A stubbed run finishes in microseconds, which rounds and clamps to one
        // second on both sides of the record.
        _logs.WithProperty("CacheState")
            .Select(entry => entry.Property("CacheState"))
            .ShouldBe(new object?[] { "cold (none on disk)", "cold (none on disk; the last cold run took 1 second)" });
    }

    [Fact]
    public async Task RunAsync_SecondWarmRunOfASolution_OpensWithoutAFigure()
    {
        // Arrange — the same pair of runs against a warm cache instead of a cold one. A warm run's duration
        // does not predict the next one's, so the second line says what state the cache is in and stops there.
        CacheHomes.PlantWarmDonor(_cacheHome, _solutionPath);
        StubExit(0);

        // Act
        await Runner().RunAsync(Config, ["inspectcode", _solutionPath], Ct);
        await Runner().RunAsync(Config, ["inspectcode", _solutionPath], Ct);

        // Assert
        IReadOnlyList<LogEntry> opened = _logs.WithProperty("CacheState");
        opened.Count.ShouldBe(2);
        var second = opened[1].Property("CacheState").ShouldBeOfType<string>();
        second.ShouldStartWith("warm (");
        second.ShouldNotContain(" took ");
    }

    [Fact]
    public async Task RunAsync_KilledAtTheCap_SaysSoRatherThanEndingWithNoLine()
    {
        // Arrange — a jb that reports 7 analysed files and is then killed at the cap.
        _processRunner
            .AnyRunOf("jb")
            .Returns<ProcessResult>(callInfo =>
            {
                Action<string> onLine = callInfo.OutputLineObserver()!;
                onLine(JbProgressLines.AnalyzingPhaseLine);
                for (var i = 0; i < 7; i++) onLine($"Analyzing File{i}.cs");

                throw new ProcessTimeoutException("'jb' timed out.");
            });

        // Act — with a progress callback that drops every line, since without one the runner counts nothing.
        await Should.ThrowAsync<UserErrorException>(() => Runner().RunAsync(Config, ["inspectcode", _solutionPath], Ct, _ => { }));

        // Assert — no exit code to report, so the cap is reported instead, at the same level, and identified
        // by RunCap alone. It carries the count as well as the message does: a UserErrorException is never
        // logged, so without it the only record of how far a killed run got dies with the response.
        LogEntry killed = _logs.ShouldHaveSingleLineWith("RunCap");
        killed.Level.ShouldBe(LogLevel.Information);
        killed.Property("Subcommand").ShouldBe("inspectcode");
        killed.Property("ElapsedMs").ShouldNotBeNull();
        killed.Property("FilesSeen").ShouldBe(7);
        _logs.WithProperty("ExitCode").ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_CancelledInFlight_SaysSoRatherThanEndingWithNoLine()
    {
        // Arrange — a jb that is cancelled while it runs: the token it was handed fires mid-run, the way a
        // stood-down pre-warm or a shutdown reaches it.
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _processRunner
            .AnyRunOf("jb")
            .Returns(callInfo =>
            {
                // The run is awaited below before the using disposes, so the capture outlives nothing.
                // ReSharper disable once AccessToDisposedClosure
                cancellation.Cancel();
                callInfo.Token().ThrowIfCancellationRequested();

                return new ProcessResult(0, string.Empty, string.Empty);
            });

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => Runner().RunAsync(Config, ["inspectcode", _solutionPath], cancellation.Token));

        // Assert — no exit code and no cap to report, so the run's end is a line of its own, at the same level,
        // carrying neither CacheState nor ExitCode nor RunCap, so it reads as none of the other three.
        LogEntry cancelled = _logs.ShouldHaveSingleLineWith("ElapsedMs");
        cancelled.Level.ShouldBe(LogLevel.Information);
        cancelled.Property("Subcommand").ShouldBe("inspectcode");
        cancelled.Property("CacheState").ShouldBeNull();
        cancelled.Property("ExitCode").ShouldBeNull();
        cancelled.Property("RunCap").ShouldBeNull();
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
        ShouldHaveSingleStartingLine().Property("RunKind").ShouldBe("speculative");
    }

    /// <summary>
    ///     Asserts that exactly one opening line was written, identified by the property only it carries, and
    ///     returns it.
    /// </summary>
    private LogEntry ShouldHaveSingleStartingLine()
    {
        return _logs.ShouldHaveSingleLineWith("CacheState");
    }

    /// <summary>Asserts that exactly one exit line was written, and returns it.</summary>
    private LogEntry ShouldHaveSingleExitLine()
    {
        return _logs.ShouldHaveSingleLineWith("ExitCode");
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