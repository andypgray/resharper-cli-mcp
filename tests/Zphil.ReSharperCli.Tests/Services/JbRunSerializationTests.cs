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
///     The lock's whole point, asserted where a caller meets it: two calls against one solution never have
///     two <c>jb</c> processes in flight at once. A second concurrent <c>jb</c> cannot share the warm
///     ReSharper cache generation — it silently forks an empty one and does the full cold analysis — so
///     overlapping runs are slower than queued ones and leave a dead cache behind. The
///     <see cref="ConcurrencyProbe" /> stands in for <c>jb</c> and fails these tests by observing an
///     overlap, not by timing.
/// </summary>
public sealed class JbRunSerializationTests : IDisposable
{
    private readonly ResolvedConfig _config;
    private readonly FakeEnvironment _environment = new();
    private readonly ConcurrencyProbe _probe = new();
    private readonly JbRunner _runner;
    private readonly string _solutionDirectory;

    public JbRunSerializationTests()
    {
        _solutionDirectory = _environment.CurrentDirectory;
        string solutionPath = Path.Combine(_solutionDirectory, "App.sln");
        File.WriteAllText(solutionPath, string.Empty);
        _config = Configs.Bare(solutionPath, _environment.CreateTempDirectory());
        _runner = JbRunners.Create(_probe);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public async Task InspectService_TwoConcurrentCallsAgainstOneSolution_RunOneAtATime()
    {
        // Arrange
        InspectService service = new(_runner);

        // Act
        await Task.WhenAll(
            service.RunAsync(_config, null, InspectSeverity.Warning, Ct),
            service.RunAsync(_config, null, InspectSeverity.Warning, Ct));

        // Assert
        _probe.Runs.ShouldBe(2);
        _probe.MaxConcurrent.ShouldBe(1);
    }

    [Fact]
    public async Task CleanupService_TwoConcurrentCallsAgainstOneSolution_RunOneAtATime()
    {
        // Arrange
        CleanupService service = new(_runner, NullLogger<CleanupService>.Instance);
        PlantFile("src/A.cs");

        // Act
        await Task.WhenAll(
            service.RunAsync(_config, ["src/A.cs"], CleanupService.DefaultProfile, Ct),
            service.RunAsync(_config, ["src/A.cs"], CleanupService.DefaultProfile, Ct));

        // Assert
        _probe.Runs.ShouldBe(2);
        _probe.MaxConcurrent.ShouldBe(1);
    }

    [Fact]
    public async Task InspectAndCleanupOfOneSolution_RunOneAtATime()
    {
        // Arrange — the two tools share one cache generation, so they contend with each other too.
        InspectService inspect = new(_runner);
        CleanupService cleanup = new(_runner, NullLogger<CleanupService>.Instance);
        PlantFile("src/A.cs");

        // Act
        Task inspectRun = inspect.RunAsync(_config, null, InspectSeverity.Warning, Ct);
        Task cleanupRun = cleanup.RunAsync(_config, ["src/A.cs"], CleanupService.DefaultProfile, Ct);
        await Task.WhenAll(inspectRun, cleanupRun);

        // Assert
        _probe.Runs.ShouldBe(2);
        _probe.MaxConcurrent.ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrencyProbe_DrivenWithNoLockInBetween_ObservesTheOverlap()
    {
        // Arrange — the guard on the three assertions above: unless the probe can actually see two runs at
        // once, "MaxConcurrent is 1" would mean nothing.

        // Act — straight at the process runner, with no JbRunner and so no lock between the callers.
        await Task.WhenAll(
            _probe.RunAsync("jb", ["inspectcode"], JbRunTimeout.Default, Ct),
            _probe.RunAsync("jb", ["inspectcode"], JbRunTimeout.Default, Ct));

        // Assert
        _probe.MaxConcurrent.ShouldBe(2);
    }

    private void PlantFile(string relativePath)
    {
        string fullPath = Path.Combine(_solutionDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "content");
    }

    /// <summary>
    ///     A <see cref="IProcessRunner" /> that records how many runs were ever in flight together and holds
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
                return new ProcessResult(0, string.Empty, string.Empty);
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