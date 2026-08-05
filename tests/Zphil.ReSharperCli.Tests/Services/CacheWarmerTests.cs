using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Services;

/// <summary>
///     The pre-warm is speculative work, so the invariant under test is what it must <em>never</em> do:
///     never run when it was turned off, never run when there is nothing to warm or the cache generation is
///     already warm or already busy, never run twice, never raise anything through the log that is not a
///     genuine surprise, and never leave a <c>jb</c> behind when the server stops. Every one of those is an
///     ordinary <see cref="WarmUpOutcome" />, which is why they can be asserted directly instead of sniffed
///     out of log lines.
/// </summary>
public sealed class CacheWarmerTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private readonly string _cacheHome;
    private readonly FakeEnvironment _environment = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly CapturingLoggerProvider _logs = new();
    private readonly JbProbe _probe = new();
    private readonly string _solutionPath;

    public CacheWarmerTests()
    {
        _solutionPath = Path.Combine(_environment.CurrentDirectory, "App.sln");
        File.WriteAllText(_solutionPath, string.Empty);

        _cacheHome = _environment.CreateTempDirectory();
        _environment.SetVariable("JB_CACHE_HOME", _cacheHome);

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(_logs);
            builder.SetMinimumLevel(LogLevel.Trace);
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _loggerFactory.Dispose();
        _environment.Dispose();
    }

    [Fact]
    public async Task Start_ColdCache_RunsAFullSolutionInspectionAndRecordsItAsWarm()
    {
        // Arrange
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert — a full solution run, because --include does not shrink jb's work, so there is no cheaper
        // shape a warm-up could take.
        warmer.Outcome.ShouldBe(WarmUpOutcome.Warmed);
        var arguments = _probe.Runs.ShouldHaveSingleItem();
        arguments[0].ShouldBe("inspectcode");
        arguments.ShouldContain(_solutionPath);
        arguments.Any(argument => argument.StartsWith("--include", StringComparison.Ordinal)).ShouldBeFalse();

        // ...and the debounce closes the loop, so the next session start finds this generation warm.
        JbWarmMarker.IsFreshWithin(_solutionPath, _cacheHome, CacheWarmer.RecentlyWarmWindow).ShouldBeTrue();
        _logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_TurnedOff_DoesNotSoMuchAsLookForJb()
    {
        // Arrange — the kill switch has to be the very first thing checked, or turning it off would still
        // cost a process spawn at every session start.
        _environment.SetVariable(CacheWarmer.EnableVariable, "off");
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert
        warmer.Outcome.ShouldBe(WarmUpOutcome.Disabled);
        _probe.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Start_NoSolutionToDiscover_ReportsNoTargetSilently()
    {
        // Arrange — a server started in a directory holding no solution, which is the ordinary case for a
        // user-scope server in a repo that is not a .NET one. Nothing to warm, and nobody to tell.
        _environment.CurrentDirectory = _environment.CreateTempDirectory();
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert
        warmer.Outcome.ShouldBe(WarmUpOutcome.NoTarget);
        _probe.Runs.ShouldBeEmpty();
        _logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_JbNotInstalled_ReportsNoTargetSilently()
    {
        // Arrange
        _probe.JbMissing = true;
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert — an uninstalled toolchain is the user's business when they call a tool, not a background
        // task's business to complain about.
        warmer.Outcome.ShouldBe(WarmUpOutcome.NoTarget);
        _probe.Runs.ShouldBeEmpty();
        _logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_MarkerStampedRecently_SkipsWithoutRunningJb()
    {
        // Arrange — something warmed this generation moments ago: a call in this session, or another
        // session entirely.
        JbWarmMarker.Stamp(_solutionPath, _cacheHome);
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert
        warmer.Outcome.ShouldBe(WarmUpOutcome.AlreadyWarm);
        _probe.Runs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_MarkerOlderThanTheWindow_WarmsAnyway()
    {
        // Arrange — aged against the shipped window itself, so the real threshold is pinned rather than an
        // injectable stand-in for it.
        JbWarmMarker.Stamp(_solutionPath, _cacheHome);
        File.SetLastWriteTimeUtc(
            JbWarmMarker.PathFor(_solutionPath, _cacheHome),
            DateTime.UtcNow - CacheWarmer.RecentlyWarmWindow - TimeSpan.FromMinutes(1));
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert
        warmer.Outcome.ShouldBe(WarmUpOutcome.Warmed);
        _probe.Runs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Start_CacheGenerationHeldByAnotherProcess_SkipsWithoutRunningJb()
    {
        // Arrange — the lock file held from outside, which is exactly what another server process's run
        // looks like to the OS. Running anyway would fork a cold cache generation, which is the whole
        // failure the run lock exists to prevent.
        await using FileStream otherProcess = new(
            JbRunLock.LockFilePathFor(_cacheHome, JbRunLock.ComputeKey(_solutionPath, _cacheHome)),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert — and it gave up rather than queueing, which is what the bounded wait above proves.
        warmer.Outcome.ShouldBe(WarmUpOutcome.Skipped);
        _probe.Runs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_CalledTwice_WarmsOnce()
    {
        // Arrange — a client that re-sends `initialized`, or a second trigger added later, must not cost a
        // second full solution analysis.
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert
        _probe.Runs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Start_JbExitsNonZero_ReportsFailedWithoutStampingOrWarning()
    {
        // Arrange — a jb that failed warmed nothing, so the next session must still try.
        _probe.ExitCode = 3;
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert — a failed background run is an outcome, not an incident: the session simply pays the cold
        // cost it would have paid anyway, and the log stays for genuine surprises.
        warmer.Outcome.ShouldBe(WarmUpOutcome.Failed);
        JbWarmMarker.IsFreshWithin(_solutionPath, _cacheHome, CacheWarmer.RecentlyWarmWindow).ShouldBeFalse();
        _logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_UnexpectedFailure_SettlesAndLogsExactlyOneWarning()
    {
        // Arrange — nothing a pre-warm can foresee, so this one *is* log-worthy.
        _probe.Fault = new InvalidOperationException("something nobody predicted");
        using CacheWarmer warmer = BuildWarmer();

        // Act
        warmer.Start();
        await warmer.Finished.WaitAsync(Generous, Ct);

        // Assert — it still settles: a fire-and-forget task that faulted silently would leave Finished
        // hanging and take host shutdown's drain with it.
        warmer.Outcome.ShouldBe(WarmUpOutcome.Failed);
        _logs.Warnings.ShouldHaveSingleItem().Exception.ShouldBe(_probe.Fault);
    }

    [Fact]
    public async Task StopAsync_WithARunInFlight_CancelsItAndReturnsWithinTheDrain()
    {
        // Arrange — a jb mid-analysis when the client disconnects. Cancelling without waiting would leave it
        // alive holding ReSharper's own cache-generation lock, which outlives our lock file and so is the one
        // orphan the run lock cannot protect the next session from.
        _probe.BlockUntilCancelled = true;
        using CacheWarmer warmer = BuildWarmer();
        warmer.Start();
        await _probe.Started.WaitAsync(Generous, Ct);

        // Act
        await warmer.StopAsync(Ct).WaitAsync(Generous, Ct);

        // Assert
        warmer.Outcome.ShouldBe(WarmUpOutcome.Cancelled);
        warmer.Finished.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task StopAsync_WithNothingEverStarted_ReturnsImmediately()
    {
        // Arrange — a session that connected but never sent `initialized`, or a host stopped before any
        // client arrived. Waiting out the drain here would add ten seconds to every such shutdown.
        using CacheWarmer warmer = BuildWarmer();

        // Act
        await warmer.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // Assert
        warmer.Outcome.ShouldBe(WarmUpOutcome.NotRun);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("on", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("something unrecognised", true)]
    [InlineData("off", false)]
    [InlineData("OFF", false)]
    [InlineData("  off  ", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("disabled", false)]
    public void IsEnabled_TreatsTheDocumentedSpellingsAsOffAndEverythingElseAsOn(string? value, bool expected)
    {
        // Assert — unrecognised falls back to the shipped default, in the same register as the log-level
        // variable. `off` is the spelling the docs teach; the rest are what a user might reasonably try.
        CacheWarmer.IsEnabled(value).ShouldBe(expected);
    }

    private CacheWarmer BuildWarmer()
    {
        return ToolHarness.BuildCacheWarmer(_probe, _environment, _loggerFactory.CreateLogger<CacheWarmer>());
    }

    /// <summary>
    ///     A scriptable <c>jb</c>: it answers the version probe so discovery succeeds, records every real run,
    ///     and can be told to report a missing toolchain, a non-zero exit, an unforeseen fault, or a run that
    ///     never finishes on its own.
    /// </summary>
    private sealed class JbProbe : IProcessRunner
    {
        private readonly List<IReadOnlyList<string>> _runs = [];
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        /// <summary>Exit code every non-probe run reports.</summary>
        public int ExitCode { get; set; }

        /// <summary>When set, both jb candidates fail their version probe and the toolchain reads as absent.</summary>
        public bool JbMissing { get; set; }

        /// <summary>When set, every non-probe run throws this instead of running.</summary>
        public Exception? Fault { get; set; }

        /// <summary>When set, every non-probe run parks until its own token is cancelled.</summary>
        public bool BlockUntilCancelled { get; set; }

        /// <summary>Completes as soon as a non-probe run has started.</summary>
        public Task Started => _started.Task;

        /// <summary>Every process this fake was asked to start, version probes included.</summary>
        public int Calls => Volatile.Read(ref _calls);

        /// <summary>The argument lists of the real runs, version probes excluded.</summary>
        public IReadOnlyList<IReadOnlyList<string>> Runs
        {
            get
            {
                lock (_runs)
                {
                    return _runs.ToList();
                }
            }
        }

        public async Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);

            if (arguments.Contains("--version"))
                return JbMissing
                    ? new ProcessResult(1, string.Empty, "jb: command not found")
                    : new ProcessResult(0, "Version: 2026.1.2", string.Empty);

            lock (_runs)
            {
                _runs.Add(arguments);
            }

            _started.TrySetResult();

            if (Fault is not null) throw Fault;

            if (BlockUntilCancelled) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            WriteEmptySarifIfRequested(arguments);
            return new ProcessResult(ExitCode, string.Empty, string.Empty);
        }

        /// <summary>inspectcode is asked for a SARIF file; the warm-up discards it, but jb would still write it.</summary>
        private static void WriteEmptySarifIfRequested(IReadOnlyList<string> arguments)
        {
            string? outputArgument = arguments.FirstOrDefault(argument => argument.StartsWith("-o=", StringComparison.Ordinal));
            if (outputArgument is null) return;

            File.WriteAllText(outputArgument["-o=".Length..], """{"runs":[{"results":[]}]}""");
        }
    }
}