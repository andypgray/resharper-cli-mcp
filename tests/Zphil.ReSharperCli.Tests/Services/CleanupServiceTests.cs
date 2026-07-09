using NSubstitute;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;

namespace Zphil.ReSharperCli.Tests.Services;

/// <summary>
///     <see cref="CleanupService" /> mutates files in place, so it validates concrete (non-wildcard) paths
///     before invoking jb. These tests plant a real solution file and real targets under a temp directory
///     so that validation is exercised against the filesystem rather than a fabricated path.
/// </summary>
public sealed class CleanupServiceTests : IDisposable
{
    private readonly ResolvedConfig _config;
    private readonly FakeEnvironment _environment = new();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly string _solutionDirectory;

    public CleanupServiceTests()
    {
        _solutionDirectory = _environment.CurrentDirectory;
        string solutionPath = Path.Combine(_solutionDirectory, "App.sln");
        File.WriteAllText(solutionPath, string.Empty);
        _config = new ResolvedConfig(solutionPath, null, "/cache", null, null, "jb");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public async Task RunAsync_SuccessfulRun_ReturnsExactSummary()
    {
        // Arrange
        PlantFile("src/A.cs");
        PlantFile("src/B.cs");
        StubExit(0);
        CleanupService service = new(_processRunner);

        // Act
        string summary = await service.RunAsync(
            _config, ["src/A.cs", "src/B.cs"], CleanupService.DefaultProfile, Ct);

        // Assert
        summary.ShouldBe(
            "Cleanup completed for 2 file(s) with profile \"Built-in: Full Cleanup\":\n"
            + "  - src/A.cs\n"
            + "  - src/B.cs");
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_ThrowsUserErrorSurfacingStderr()
    {
        // Arrange
        PlantFile("A.cs");
        _processRunner
            .RunAsync("jb", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(1, string.Empty, "Unknown profile 'No Such Profile'"));
        CleanupService service = new(_processRunner);

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => service.RunAsync(_config, ["A.cs"], "No Such Profile", Ct));

        // Assert
        exception.Message.ShouldContain("Unknown profile 'No Such Profile'");
    }

    [Fact]
    public async Task RunAsync_MissingPlainFile_ThrowsNamingItAndDoesNotInvokeJb()
    {
        // Arrange — no file planted, so the concrete path does not exist.
        CleanupService service = new(_processRunner);

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => service.RunAsync(_config, ["src/Missing.cs"], CleanupService.DefaultProfile, Ct));

        // Assert
        exception.Message.ShouldContain("src/Missing.cs");
        await _processRunner.DidNotReceive().RunAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WildcardEntry_SkipsValidationAndRunsJb()
    {
        // Arrange — a wildcard pattern is handed to jb unvalidated, even though nothing matches it on disk.
        StubExit(0);
        CleanupService service = new(_processRunner);

        // Act
        string summary = await service.RunAsync(_config, ["src/**/*.cs"], CleanupService.DefaultProfile, Ct);

        // Assert
        summary.ShouldContain("src/**/*.cs");
        await _processRunner.Received(1).RunAsync(
            "jb", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_AbsoluteExistingPath_IsAccepted()
    {
        // Arrange
        string absolute = PlantFile("src/Real.cs");
        StubExit(0);
        CleanupService service = new(_processRunner);

        // Act
        string summary = await service.RunAsync(_config, [absolute], CleanupService.DefaultProfile, Ct);

        // Assert
        summary.ShouldContain(absolute);
        await _processRunner.Received(1).RunAsync(
            "jb", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    private string PlantFile(string relativePath)
    {
        string fullPath = Path.Combine(_solutionDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, string.Empty);
        return fullPath;
    }

    private void StubExit(int exitCode)
    {
        _processRunner
            .RunAsync("jb", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(exitCode, string.Empty, string.Empty));
    }
}