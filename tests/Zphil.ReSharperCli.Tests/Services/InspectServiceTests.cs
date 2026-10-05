using NSubstitute;
using NSubstitute.Core;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Sarif;
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
            outputPath = JbStubs.OutputPathOf(callInfo.Arguments())!;
            File.WriteAllText(outputPath, sarif);
            return JbStubs.Success;
        });

        // Act
        IReadOnlyList<InspectIssue> issues = await _service.RunAsync(_config, null, InspectSeverity.Warning, Ct);

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
            outputPath = JbStubs.OutputPathOf(callInfo.Arguments())!;
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
        // Arrange
        StubRun(callInfo =>
        {
            JbStubs.WriteSarifIfRequested(callInfo.Arguments(), "{ this is not valid SARIF json");
            return JbStubs.Success;
        });

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _service.RunAsync(_config, null, InspectSeverity.Warning, Ct));

        // Assert
        exception.Message.ShouldContain("SARIF");
    }

    private void StubRun(Func<CallInfo, ProcessResult> behavior)
    {
        _processRunner
            .AnyRunOf("jb")
            .Returns(callInfo => behavior(callInfo));
    }
}