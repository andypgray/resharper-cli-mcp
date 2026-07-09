using System.ComponentModel;
using System.Diagnostics;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     The one test class that spawns real OS processes, exercising <see cref="ProcessRunner" /> against
///     actual exit codes, output capture, timeouts, and a missing executable.
/// </summary>
public sealed class ProcessRunnerTests
{
    private static readonly TimeSpan GenerousTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task RunAsync_SuccessfulCommand_ReturnsZeroExitAndCapturesStdout()
    {
        // Arrange
        ProcessRunner runner = new();

        // Act
        ProcessResult result = await runner.RunAsync("dotnet", ["--version"], GenerousTimeout, TestContext.Current.CancellationToken);

        // Assert
        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_ReturnsExitCodeWithoutThrowing()
    {
        // Arrange
        ProcessRunner runner = new();
        (string fileName, string[] arguments) = ExitWithCodeCommand(7);

        // Act
        ProcessResult result = await runner.RunAsync(fileName, arguments, GenerousTimeout, TestContext.Current.CancellationToken);

        // Assert
        result.ExitCode.ShouldBe(7);
    }

    [Fact]
    public async Task RunAsync_ProcessExceedsTimeout_ThrowsUserErrorException()
    {
        // Arrange
        ProcessRunner runner = new();
        (string fileName, string[] arguments) = SleepThirtySecondsCommand();

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => runner.RunAsync(fileName, arguments, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        // Assert
        exception.Message.ShouldContain("timed out after 2 seconds");
        exception.Message.ShouldContain(fileName);
    }

    [Fact]
    public async Task RunAsync_OrphanChildHoldsStdout_ReturnsPromptlyInsteadOfHangingOnDrain()
    {
        // Arrange — the parent exits at once but leaves a background child holding the stdout pipe open
        // for 30 s. The bounded drain must cap on the timeout rather than block waiting for EOF.
        ProcessRunner runner = new();
        (string fileName, string[] arguments) = OrphanHoldingStdoutCommand();
        var stopwatch = Stopwatch.StartNew();

        // Act
        ProcessResult result = await runner.RunAsync(fileName, arguments, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        stopwatch.Stop();

        // Assert — the parent's real exit code is returned, and the call unblocked long before the 30 s orphan.
        result.ExitCode.ShouldBe(0);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(25));
    }

    [Theory]
    [InlineData(1, "1 second")]
    [InlineData(30, "30 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(90, "2 minutes")]
    [InlineData(300, "5 minutes")]
    public void FormatDuration_RendersWholeUnitsWithCorrectPluralization(int seconds, string expected)
    {
        // Act
        string formatted = ProcessRunner.FormatDuration(TimeSpan.FromSeconds(seconds));

        // Assert
        formatted.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_MissingExecutable_ThrowsWin32Exception()
    {
        // Arrange
        ProcessRunner runner = new();

        // Act / Assert
        await Should.ThrowAsync<Win32Exception>(() => runner.RunAsync("this-executable-does-not-exist-9f3a1c", [], GenerousTimeout, TestContext.Current.CancellationToken));
    }

    private static (string FileName, string[] Arguments) ExitWithCodeCommand(int code)
    {
        return OperatingSystem.IsWindows()
            ? ("cmd", ["/c", $"exit {code}"])
            : ("sh", ["-c", $"exit {code}"]);
    }

    private static (string FileName, string[] Arguments) SleepThirtySecondsCommand()
    {
        return OperatingSystem.IsWindows()
            ? ("ping", ["-n", "30", "127.0.0.1"])
            : ("sleep", ["30"]);
    }

    private static (string FileName, string[] Arguments) OrphanHoldingStdoutCommand()
    {
        return OperatingSystem.IsWindows()
            ? ("cmd", ["/c", "start /b ping -n 30 127.0.0.1"])
            : ("sh", ["-c", "sleep 30 & exit 0"]);
    }
}