using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Services;

/// <summary>
///     Asserted where a caller meets it: two calls through this server never have two <c>jb</c> processes in
///     flight at once, whatever solutions they are against.
/// </summary>
/// <remarks>
///     Against <em>one</em> solution that is <see cref="JbRunLock" />'s doing; against two it is
///     <see cref="JbRunSlot" />'s, since different solutions are different generations and pass the lock
///     uncontended. The <see cref="ConcurrencyProbe" /> stands in for <c>jb</c> and fails these tests by
///     observing an overlap, not by timing.
/// </remarks>
public sealed class JbRunnerSerializationTests : IDisposable
{
    private readonly ResolvedConfig _config;
    private readonly FakeEnvironment _environment = new();
    private readonly ConcurrencyProbe _probe = new();
    private readonly JbRunner _runner;
    private readonly string _solutionDirectory;

    public JbRunnerSerializationTests()
    {
        _solutionDirectory = _environment.CurrentDirectory;
        string solutionPath = _environment.PlantSolution("App.sln");
        _config = Configs.Bare(solutionPath, _environment.CreateTempDirectory());
        _runner = JbRunners.Create(_probe);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public async Task InspectAndCleanupOfOneSolution_RunOneAtATime()
    {
        // Arrange — the two tools share one cache generation, so they contend with each other too. The
        // serialization lives entirely in JbRunner, so the only way either service escapes it is by bypassing
        // the runner, and one call through each is what catches either of them doing so.
        InspectService inspect = new(_runner);
        CleanupService cleanup = new(_runner, NullLogger<CleanupService>.Instance);
        SolutionFiles.Plant(_solutionDirectory, "src/A.cs", "content");

        // Act
        Task inspectRun = inspect.RunAsync(_config, null, InspectSeverity.Warning, Ct);
        Task cleanupRun = cleanup.RunAsync(_config, ["src/A.cs"], CleanupService.DefaultProfile, Ct);
        await Task.WhenAll(inspectRun, cleanupRun);

        // Assert
        _probe.Runs.ShouldBe(2);
        _probe.MaxConcurrent.ShouldBe(1);
    }

    [Fact]
    public async Task InspectOfOneSolutionAndCleanupOfAnother_RunOneAtATime()
    {
        // Arrange — a second solution in a directory of its own, so the two calls take different cache
        // generations and the lock lets both straight through. Run together, jb runs against different
        // solutions slow each other by multiples, enough to carry a one-file cleanup past the default cap.
        InspectService inspect = new(_runner);
        CleanupService cleanup = new(_runner, NullLogger<CleanupService>.Instance);

        string otherSolutionPath = _environment.CreateCheckout("Other.sln");
        ResolvedConfig otherConfig = Configs.Bare(otherSolutionPath, _environment.CreateTempDirectory());
        string otherFile = Path.Combine(Path.GetDirectoryName(otherSolutionPath)!, "B.cs");
        await File.WriteAllTextAsync(otherFile, "content", Ct);

        // Act
        Task inspectRun = inspect.RunAsync(_config, null, InspectSeverity.Warning, Ct);
        Task cleanupRun = cleanup.RunAsync(otherConfig, ["B.cs"], CleanupService.DefaultProfile, Ct);
        await Task.WhenAll(inspectRun, cleanupRun);

        // Assert
        _probe.Runs.ShouldBe(2);
        _probe.MaxConcurrent.ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrencyProbe_DrivenWithNoLockInBetween_ObservesTheOverlap()
    {
        // Arrange — the guard on the MaxConcurrent assertions above: unless the probe can actually see two
        // runs at once, "MaxConcurrent is 1" would mean nothing.

        // Act — straight at the process runner, with no JbRunner and so no lock between the callers.
        await Task.WhenAll(
            _probe.RunAsync("jb", ["inspectcode"], JbRunTimeout.Default, Ct),
            _probe.RunAsync("jb", ["inspectcode"], JbRunTimeout.Default, Ct));

        // Assert
        _probe.MaxConcurrent.ShouldBe(2);
    }

    /// <summary>
    ///     An <see cref="IProcessRunner" /> that records how many runs were ever in flight together and holds
    ///     each one long enough that unserialized callers would demonstrably overlap.
    /// </summary>
    private sealed class ConcurrencyProbe : IProcessRunner
    {
        private readonly Lock _gate = new();
        private int _inFlight;

        public int Runs { get; private set; }

        public int MaxConcurrent { get; private set; }

        public async Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            Action<string>? onOutputLine = null)
        {
            lock (_gate)
            {
                Runs++;
                _inFlight++;
                MaxConcurrent = Math.Max(MaxConcurrent, _inFlight);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                JbStubs.WriteEmptySarifIfRequested(arguments);
                return JbStubs.Success;
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight--;
                }
            }
        }
    }
}