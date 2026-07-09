using NSubstitute;
using NSubstitute.Core;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Services;

public sealed class InspectServiceTests
{
    private static readonly ResolvedConfig Config =
        new("/sln/App.sln", null, "/cache", null, null, "jb");

    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
        InspectService service = new(_processRunner);

        // Act
        var issues = await service.RunAsync(Config, null, "WARNING", Ct);

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
        InspectService service = new(_processRunner);

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => service.RunAsync(Config, null, "WARNING", Ct));

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
        InspectService service = new(_processRunner);

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => service.RunAsync(Config, null, "WARNING", Ct));

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
        InspectService service = new(_processRunner);

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => service.RunAsync(Config, null, "WARNING", Ct));

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