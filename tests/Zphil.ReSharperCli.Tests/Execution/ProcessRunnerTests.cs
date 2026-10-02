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

    /// <summary>
    ///     The orphan-drain case's cap: the parent's whole budget to start, launch its orphan and exit. The drain
    ///     after the exit is bounded by the same cap, so the call returns at it however quickly the parent went,
    ///     which also makes this how long the case takes.
    /// </summary>
    /// <remarks>
    ///     Ten seconds is over five times the worst start-to-exit time measured on Windows beside three concurrent
    ///     cold <c>jb</c> runs, a load under which three seconds was once exceeded; it has not been measured on
    ///     Linux.
    /// </remarks>
    private static readonly TimeSpan ParentStartBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     How long the orphan holds standard output: four budgets, so a call that waited for it cannot pass for
    ///     one that returned at the cap, yet short enough that an orphan which escaped the job (it can, being
    ///     bound only after it starts) frees the reader threads it blocks within a minute.
    /// </summary>
    private static readonly TimeSpan OrphanHoldsPipeFor = ParentStartBudget * 4;

    /// <summary>How early a timer can fire against the stopwatch: the system clock's tick, with room to spare.</summary>
    private static readonly TimeSpan TimerSlack = TimeSpan.FromMilliseconds(500);

    private readonly ChildProcessLifetime _lifetime = new(new SystemEnvironment(), NullLogger<ChildProcessLifetime>.Instance);

    /// <summary>Holds the files children print back, deleted whole with the fixture.</summary>
    private readonly DirectoryInfo _scratch = Directory.CreateTempSubdirectory("resharper-cli-lines-");

    /// <summary>Read by <c>SkipUnless</c> on the case that pins the binding.</summary>
    public static bool OnWindows => OperatingSystem.IsWindows();

    public void Dispose()
    {
        _lifetime.Dispose();

        try
        {
            _scratch.Delete(true);
        }
        catch
        {
            // Best-effort: a leftover temp directory is not worth failing a green run over.
        }
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
    public async Task RunAsync_OrphanChildHoldsStdout_ReturnsAtTheCapRatherThanWaitingForTheOrphan()
    {
        // Arrange — the parent exits at once but leaves a background child holding the stdout pipe open past
        // the cap. The drain after the exit is bounded by the same cap, so the call returns at the cap rather
        // than blocking until EOF.
        ProcessRunner runner = Runner();
        (string fileName, string[] arguments) = OrphanHoldingStdoutCommand(OrphanHoldsPipeFor);
        var stopwatch = Stopwatch.StartNew();

        // Act
        ProcessResult? result = null;
        await Should.NotThrowAsync(
            async () => result = await runner.RunAsync(
                fileName, arguments, ParentStartBudget, TestContext.Current.CancellationToken),
            "the parent did not start and exit inside its budget: a slow start, not a drain hang");
        stopwatch.Stop();

        // Assert — the parent's real exit code, a return no earlier than the cap, which is what proves the orphan
        // held the pipe at all, and a return well before the orphan let go.
        result.ShouldNotBeNull().ExitCode.ShouldBe(0);
        stopwatch.Elapsed.ShouldBeGreaterThanOrEqualTo(
            ParentStartBudget - TimerSlack,
            "returned before the cap, so nothing held the pipe and the bounded drain was never exercised");
        stopwatch.Elapsed.ShouldBeLessThan(
            OrphanHoldsPipeFor - TimeSpan.FromSeconds(5),
            "waited for the orphan to let go of the pipe rather than returning at the cap");
    }

    [Fact]
    public async Task RunAsync_WithALineObserver_HandsOverEachLineWithoutItsCarriageReturn()
    {
        // Arrange — three CRLF lines from a real child over a real pipe, because what this is testing is chunk
        // boundaries and line endings, and a fake stream would supply neither.
        ProcessRunner runner = Runner();
        List<string> lines = [];
        (string fileName, string[] arguments) = PrintLinesCommand(["Analyzing files", "Analyzing A.cs", "Analyzing B.cs"]);

        // Act
        ProcessResult result = await runner.RunAsync(
            fileName, arguments, GenerousTimeout, TestContext.Current.CancellationToken, lines.Add);

        // Assert — every line arrives, in order, with no stray carriage return for a consumer to trim.
        result.ExitCode.ShouldBe(0);
        lines.ShouldBe(["Analyzing files", "Analyzing A.cs", "Analyzing B.cs"]);
    }

    [Fact]
    public async Task RunAsync_WithALineObserver_StillCapturesTheWholeOutput()
    {
        // Arrange — observing must not consume: inspect's SARIF comes from a file, but a failed run's message
        // quotes the captured text, and a cleanup classifies nothing without its exit code.
        ProcessRunner runner = Runner();
        (string fileName, string[] arguments) = PrintLinesCommand(["first", "second"]);

        // Act
        ProcessResult result = await runner.RunAsync(
            fileName, arguments, GenerousTimeout, TestContext.Current.CancellationToken, _ => { });

        // Assert
        result.StandardOutput.ShouldContain("first");
        result.StandardOutput.ShouldContain("second");
    }

    [Fact]
    public async Task RunAsync_ALineLongerThanOneRead_ArrivesWholeRatherThanInPieces()
    {
        // Arrange — reads come off the pipe in 8192-char chunks, so a line longer than one of them is
        // guaranteed to straddle a boundary. Without the carry, the observer would see two half-lines and
        // classify neither.
        ProcessRunner runner = Runner();
        var payload = new string('x', 9000);
        (string fileName, string[] arguments) = PrintLinesCommand([$"Analyzing {payload}", "Analyzing B.cs"]);
        List<string> lines = [];

        // Act
        await runner.RunAsync(fileName, arguments, GenerousTimeout, TestContext.Current.CancellationToken, lines.Add);

        // Assert — the long line is bounded rather than unbounded (a stream with no newline at all must not
        // grow the carry without limit), but it is still one line and still classifies as one file.
        List<string> analyzed = lines.Where(line => line.StartsWith("Analyzing ", StringComparison.Ordinal)).ToList();
        analyzed.Count.ShouldBe(2);
        analyzed[0].Length.ShouldBeGreaterThan(8000);
        analyzed[1].ShouldBe("Analyzing B.cs");
    }

    [Fact]
    public async Task RunAsync_AnObserverThatThrows_DoesNotStopTheRunOrTheDrain()
    {
        // Arrange — the observer runs on the loop that keeps the child from blocking on a full pipe, so a
        // throw there must not be able to wedge or fail the run.
        ProcessRunner runner = Runner();
        (string fileName, string[] arguments) = PrintLinesCommand(["first", "second"]);

        // Act
        ProcessResult result = await runner.RunAsync(
            fileName,
            arguments,
            GenerousTimeout,
            TestContext.Current.CancellationToken,
            _ => throw new InvalidOperationException("the client went away"));

        // Assert
        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldContain("second");
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

    /// <summary>
    ///     A parent that exits at once, leaving a child that holds standard output open for
    ///     <paramref name="holdFor" />. <c>ping</c> waits a second between echoes, so <c>n</c> echoes take
    ///     <c>n − 1</c> seconds; <c>/d</c> keeps a machine's AutoRun commands out of the parent's start budget.
    /// </summary>
    private static (string FileName, string[] Arguments) OrphanHoldingStdoutCommand(TimeSpan holdFor)
    {
        var seconds = (int)holdFor.TotalSeconds;

        return OperatingSystem.IsWindows()
            ? ("cmd", ["/d", "/c", $"start /b ping -n {seconds + 1} 127.0.0.1"])
            : ("sh", ["-c", $"sleep {seconds} & exit 0"]);
    }

    /// <summary>
    ///     A child that writes <paramref name="lines" /> to standard output, CRLF-terminated, by printing a
    ///     file this test wrote.
    /// </summary>
    /// <remarks>
    ///     A file rather than a chain of <c>echo</c>s, and the two reasons are the two things worth testing
    ///     here. <c>cmd</c>'s command line is capped at about 8,191 characters, which is the very length a
    ///     line has to exceed to straddle a read; and <c>echo x &amp; echo y</c> emits the space before the
    ///     separator, so what the child wrote would not be what the test asked for. CRLF on every platform is
    ///     deliberate too — it is what <c>jb</c> writes, and it makes the carriage-return trim a real
    ///     assertion rather than a Windows-only one.
    /// </remarks>
    private (string FileName, string[] Arguments) PrintLinesCommand(IReadOnlyList<string> lines)
    {
        string path = Path.Combine(_scratch.FullName, $"{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, string.Concat(lines.Select(line => line + "\r\n")));

        return OperatingSystem.IsWindows() ? ("cmd", ["/c", "type", path]) : ("cat", [path]);
    }
}