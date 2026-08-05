using NSubstitute;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;

namespace Zphil.ReSharperCli.Tests.Services;

/// <summary>
///     What <see cref="JbRunner" /> owns on behalf of both services: naming the failed subcommand in the
///     error, and bounding how much of a failed run's standard error comes back with it.
/// </summary>
public sealed class JbRunnerTests : IDisposable
{
    private readonly ResolvedConfig _config;
    private readonly FakeEnvironment _environment = new();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly JbRunner _runner;

    public JbRunnerTests()
    {
        _config = new ResolvedConfig("/sln/App.sln", null, null, _environment.CreateTempDirectory(), null, null, "jb");
        _runner = new JbRunner(_processRunner, new JbRunLock());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_NamesTheSubcommandItWasGiven()
    {
        // Arrange — the subcommand comes from the argument list rather than a parameter, so it cannot drift
        // from what jb was actually asked to do.
        StubExit(3, "profile not found");

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["cleanupcode", _config.SolutionPath], Ct));

        // Assert
        exception.Message.ShouldStartWith("jb cleanupcode exited with code 3.");
        exception.Message.ShouldContain("profile not found");
    }

    [Fact]
    public async Task RunAsync_VeryLongStandardError_KeepsOnlyTheTail()
    {
        // Arrange — jb can emit megabytes on a bad run; only the end of it diagnoses anything, and the
        // response budget is not the place to find that out.
        string noise = new('x', 5000);
        StubExit(1, noise + "the actual failure");

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _runner.RunAsync(_config, ["inspectcode", _config.SolutionPath], Ct));

        // Assert
        exception.Message.ShouldContain("the actual failure");
        exception.Message.Length.ShouldBeLessThan(2500);
    }

    private void StubExit(int exitCode, string standardError)
    {
        _processRunner
            .RunAsync("jb", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(exitCode, string.Empty, standardError));
    }
}