using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Infrastructure;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     The one test class that spawns real OS processes, exercising <see cref="ProcessRunner" /> against
///     actual exit codes, output capture, timeouts, and a missing executable.
/// </summary>
/// <remarks>
///     Every case here runs through a real <see cref="ChildProcessLifetime" /> over the real environment,
///     which on Linux means every spawn is genuinely wrapped in <c>setpriv</c>. That is deliberate: the
///     timeout and missing-executable cases are the regression guard for the two promises the wrapper makes —
///     that a command that cannot be resolved is left alone, and that a message about a run names the program
///     its caller asked for — and a lifetime built over an empty <c>PATH</c> would guard neither.
/// </remarks>
public sealed class ProcessRunnerTests : IDisposable
{
    private static readonly TimeSpan GenerousTimeout = TimeSpan.FromSeconds(30);

    private readonly ChildProcessLifetime _lifetime = new(new SystemEnvironment(), NullLogger<ChildProcessLifetime>.Instance);

    /// <summary>Read by <c>SkipUnless</c> on the case that pins the binding.</summary>
    public static bool OnWindows => OperatingSystem.IsWindows();

    public void Dispose()
    {
        _lifetime.Dispose();
    }

    [Fact]
    public async Task RunAsync_SuccessfulCommand_ReturnsZeroExitAndCapturesStdout()
    {
        // Arrange
        ProcessRunner runner = Runner();

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
        ProcessRunner runner = Runner();
        (string fileName, string[] arguments) = ExitWithCodeCommand(7);

        // Act
        ProcessResult result = await runner.RunAsync(fileName, arguments, GenerousTimeout, TestContext.Current.CancellationToken);

        // Assert
        result.ExitCode.ShouldBe(7);
    }

    [Fact]
    public async Task RunAsync_ProcessExceedsTimeout_ThrowsProcessTimeoutException()
    {
        // Arrange
        ProcessRunner runner = Runner();
        (string fileName, string[] arguments) = SleepThirtySecondsCommand();

        // Act — the distinct type is what lets the caller that chose the timeout recognise its own cap and
        // restate it; a plain UserErrorException would leave the mechanical message as the last word.
        var exception = await Should.ThrowAsync<ProcessTimeoutException>(() => runner.RunAsync(fileName, arguments, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        // Assert — the name is the caller's, not the platform wrapper's: on Linux this spawn is setpriv, and
        // a message saying so would name a program nothing above here has ever heard of.
        exception.ShouldBeAssignableTo<UserErrorException>();
        exception.Message.ShouldContain("timed out after 2 seconds");
        exception.Message.ShouldContain(fileName);
    }

    [Fact]
    public async Task RunAsync_OrphanChildHoldsStdout_ReturnsPromptlyInsteadOfHangingOnDrain()
    {
        // Arrange — the parent exits at once but leaves a background child holding the stdout pipe open
        // for 30 s. The bounded drain must cap on the timeout rather than block waiting for EOF.
        ProcessRunner runner = Runner();
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
    [InlineData(300, "5 minutes")]
    [InlineData(600, "10 minutes")]
    public void FormatDuration_WholeUnits_RendersThemWithCorrectPluralization(int seconds, string expected)
    {
        // Act
        string formatted = ProcessRunner.FormatDuration(TimeSpan.FromSeconds(seconds));

        // Assert
        formatted.ShouldBe(expected);
    }

    [Theory]
    [InlineData(61, "1 minute 1 second")]
    [InlineData(90, "1 minute 30 seconds")]
    [InlineData(455, "7 minutes 35 seconds")]
    public void FormatDuration_LeftoverSeconds_SaysThemRatherThanRoundingIntoTheMinutes(int seconds, string expected)
    {
        // The run cap is configured in seconds, so rounding 90 up to "2 minutes" would report a cap the
        // user never set and quietly contradict the value in their own config.
        ProcessRunner.FormatDuration(TimeSpan.FromSeconds(seconds)).ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_MissingExecutable_ThrowsWin32Exception()
    {
        // Arrange
        ProcessRunner runner = Runner();

        // Act / Assert — unchanged by the wrapper, and that is the point of decline-on-unresolvable: this is
        // the shape JbLocator reads as "candidate failed" when jb is not installed.
        await Should.ThrowAsync<Win32Exception>(() => runner.RunAsync("this-executable-does-not-exist-9f3a1c", [], GenerousTimeout, TestContext.Current.CancellationToken));
    }

    [Fact(Skip = "The job object is a Windows primitive.", SkipUnless = nameof(OnWindows))]
    public async Task RunAsync_OnWindows_BindsTheChildToTheJobThatDiesWithThisServer()
    {
        // Arrange — its own lifetime, because the line naming the binding is written by the lifetime rather
        // than by the runner.
        CapturingLoggerProvider logs = new();
        using ChildProcessLifetime lifetime = new(new SystemEnvironment(), Logs.Capturing(logs).CreateLogger<ChildProcessLifetime>());
        ProcessRunner runner = new(lifetime, NullLogger<ProcessRunner>.Instance);
        (string fileName, string[] arguments) = ExitWithCodeCommand(0);

        // Act
        await runner.RunAsync(fileName, arguments, GenerousTimeout, TestContext.Current.CancellationToken);

        // Assert — a child bound to the job dies with this server however it dies. Without the line, the one
        // question a field log cannot answer is whether a particular spawn was covered.
        LogEntry spawned = logs.WithProperty("OrphanGuard").ShouldHaveSingleItem();
        spawned.Property("OrphanGuard").ShouldBe(ChildProcessLifetime.KillOnJobClose);
        spawned.Property("ChildFileName").ShouldBe(fileName);
    }

    private ProcessRunner Runner()
    {
        return new ProcessRunner(_lifetime, NullLogger<ProcessRunner>.Instance);
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