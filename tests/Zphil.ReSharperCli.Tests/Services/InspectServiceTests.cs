using NSubstitute;
using NSubstitute.Core;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Services;

public sealed class InspectServiceTests : IDisposable
{
    private readonly ResolvedConfig _config;
    private readonly FakeEnvironment _environment = new();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly InspectService _service;

    public InspectServiceTests()
    {
        // The cache home is a real directory: JbRunLock creates it and takes its lock file there, so a
        // literal like "/cache" would leave a stray folder at the drive root.
        _config = Configs.Bare("/sln/App.sln", _environment.CreateTempDirectory());
        _service = new InspectService(JbRunners.Create(_processRunner));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public async Task RunAsync_SuccessfulRun_ParsesSarifAndCleansUpTempDirectory()
    {
        // Arrange
        string sarif = Fixtures.ReadSarif("inspect-sample.json");
        string? outputPath = null;
        StubRun(callInfo =>
        {
            outputPath = OutputPathFrom(callInfo.ArgAt<IReadOnlyList<string>>(1));
            File.WriteAllText(outputPath, sarif);
            return new ProcessResult(0, string.Empty, string.Empty);
        });

        // Act
        var issues = await _service.RunAsync(_config, null, InspectSeverity.Warning, Ct);

        // Assert
        issues.Count.ShouldBe(3);
        outputPath.ShouldNotBeNull();
        Directory.Exists(Path.GetDirectoryName(outputPath!)).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_ThrowsUserErrorWithStderrAndCleansUpTempDirectory()
    {
        // Arrange
        string? outputPath = null;
        StubRun(callInfo =>
        {
            outputPath = OutputPathFrom(callInfo.ArgAt<IReadOnlyList<string>>(1));
            return new ProcessResult(5, string.Empty, "boom: analysis failed");
        });

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _service.RunAsync(_config, null, InspectSeverity.Warning, Ct));

        // Assert
        exception.Message.ShouldContain("5");
        exception.Message.ShouldContain("boom: analysis failed");
        outputPath.ShouldNotBeNull();
        Directory.Exists(Path.GetDirectoryName(outputPath!)).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_ExitZeroButNoOutputFile_ThrowsUserError()
    {
        // Arrange
        StubRun(_ => new ProcessResult(0, string.Empty, "jb produced no output"));

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _service.RunAsync(_config, null, InspectSeverity.Warning, Ct));

        // Assert
        exception.Message.ShouldContain("did not produce an output file");
    }

    [Fact]
    public async Task RunAsync_UnparseableSarifOutput_ThrowsUserErrorMentioningSarif()
    {
        // Arrange — jb exits 0 and writes an output file, but its contents are not valid JSON.
        StubRun(callInfo =>
        {
            string outputPath = OutputPathFrom(callInfo.ArgAt<IReadOnlyList<string>>(1));
            File.WriteAllText(outputPath, "{ this is not valid SARIF json");
            return new ProcessResult(0, string.Empty, string.Empty);
        });

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _service.RunAsync(_config, null, InspectSeverity.Warning, Ct));

        // Assert
        exception.Message.ShouldContain("SARIF");
    }

    private void StubRun(Func<CallInfo, ProcessResult> behavior)
    {
        _processRunner
            .RunAsync("jb", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => behavior(callInfo));
    }

    private static string OutputPathFrom(IReadOnlyList<string> arguments)
    {
        string arg = arguments.First(a => a.StartsWith("-o=", StringComparison.Ordinal));
        return arg["-o=".Length..];
    }
}