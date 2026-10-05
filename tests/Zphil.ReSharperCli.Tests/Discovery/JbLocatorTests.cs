using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Discovery;

public sealed class JbLocatorTests : IDisposable
{
    private const string VersionOutput =
        "JetBrains Inspect Code 2026.1.2\nRunning on x64 OS in x64 architecture\nVersion: 2026.1.2\n";

    /// <summary>A candidate answering the probe the way a healthy <c>jb</c> does, banner and all.</summary>
    private static readonly ProcessResult Healthy = new(0, VersionOutput, string.Empty);

    private readonly FakeEnvironment _environment = new();

    private readonly CapturingLoggerProvider _logs = new();

    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private string DotnetToolsCandidate =>
        Path.Combine(_environment.HomeDirectory, ".dotnet", "tools", OperatingSystem.IsWindows() ? "jb.exe" : "jb");

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public async Task LocateAsync_JbOnPath_ReturnsPathCandidateWithParsedVersion()
    {
        // Arrange
        Probe("jb").Returns(Healthy);
        JbLocator locator = Locator();

        // Act
        JbInstallation installation = await locator.LocateAsync(Ct);

        // Assert
        installation.ExecutablePath.ShouldBe("jb");
        installation.Version.ShouldBe("2026.1.2");
    }

    [Fact]
    public async Task LocateAsync_JbNotOnPath_FallsBackToDotnetToolsCandidate()
    {
        // Arrange
        Probe("jb").Throws(new Win32Exception("The system cannot find the file specified."));
        Probe(DotnetToolsCandidate).Returns(Healthy);
        JbLocator locator = Locator();

        // Act
        JbInstallation installation = await locator.LocateAsync(Ct);

        // Assert
        installation.ExecutablePath.ShouldBe(DotnetToolsCandidate);
    }

    [Fact]
    public async Task LocateAsync_NoVersionLine_UsesTrimmedStdoutAsVersion()
    {
        // Arrange
        Probe("jb").Returns(new ProcessResult(0, "  ReSharper CLI build 12345  \n", string.Empty));
        JbLocator locator = Locator();

        // Act
        JbInstallation installation = await locator.LocateAsync(Ct);

        // Assert
        installation.Version.ShouldBe("ReSharper CLI build 12345");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("Version:   \n")]
    public async Task LocateAsync_ProbeExitsZeroWithoutAVersion_TreatsCandidateAsFailed(string? standardOutput)
    {
        // Arrange — jb that exits cleanly and reports nothing identifiable is not a jb worth running. The
        // null row is the shape a defaulted ProcessResult carries, which is how this was found.
        _processRunner
            .AnyRun()
            .Returns(new ProcessResult(0, standardOutput!, string.Empty));
        JbLocator locator = Locator();

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => locator.LocateAsync(Ct));

        // Assert — it ran, so the remedy is not to install it again.
        exception.Message.ShouldStartWith("JetBrains ReSharper CLI tools found, but no candidate reported a version.");
        exception.Message.ShouldContain("exited with code 0 but reported no version");
        exception.Message.ShouldNotContain("dotnet tool install");
    }

    [Theory]
    [InlineData(0, "")] // exits cleanly but reports no version
    [InlineData(1, "some jb error")] // exits non-zero
    public async Task LocateAsync_FirstCandidateAnswersWithoutAVersion_FallsBackToNextCandidate(
        int exitCode, string standardError)
    {
        // Arrange
        Probe("jb").Returns(new ProcessResult(exitCode, string.Empty, standardError));
        Probe(DotnetToolsCandidate).Returns(Healthy);
        JbLocator locator = Locator();

        // Act
        JbInstallation installation = await locator.LocateAsync(Ct);

        // Assert
        installation.ExecutablePath.ShouldBe(DotnetToolsCandidate);
        installation.Version.ShouldBe("2026.1.2");
    }

    [Fact]
    public async Task LocateAsync_FirstCandidateTimesOut_StillProbesTheNextAndReturnsIt()
    {
        // Arrange — the field shape: two servers starting at once on a busy machine, the PATH probe killed at
        // the cap, and the next candidate answering inside it. A timeout settles the remedy only once every
        // candidate has failed, so stopping at the first would turn this recovered call into an error.
        Probe("jb").Throws(new ProcessTimeoutException("'jb' timed out after 30 seconds."));
        Probe(DotnetToolsCandidate).Returns(Healthy);
        JbLocator locator = Locator();

        // Act
        JbInstallation installation = await locator.LocateAsync(Ct);

        // Assert
        installation.ExecutablePath.ShouldBe(DotnetToolsCandidate);
        installation.Version.ShouldBe("2026.1.2");
        await _processRunner.Received(1).AnyRunOf(DotnetToolsCandidate);
    }

    [Fact]
    public async Task LocateAsync_NoCandidateCanBeStarted_ThrowsWithInstallGuidanceNamingBothCandidates()
    {
        // Arrange
        _processRunner
            .AnyRun()
            .Throws(new Win32Exception("The system cannot find the file specified."));
        JbLocator locator = Locator();

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => locator.LocateAsync(Ct));

        // Assert
        exception.Message.ShouldStartWith("JetBrains ReSharper CLI tools not found.");
        exception.Message.ShouldContain("dotnet tool install JetBrains.ReSharper.GlobalTools -g");
        exception.Message.ShouldContain("jb:");
        exception.Message.ShouldContain(DotnetToolsCandidate);
    }

    [Fact]
    public async Task LocateAsync_EveryProbeTimesOut_SaysJbIsInstalledAndNamesEachCandidateOnce()
    {
        // Arrange — the field shape: two servers starting at once, both probes killed at the cap by a busy
        // machine. Every candidate failed, but a process has to start before it can be killed, so telling
        // this session to install jb would send it to fix a tool it already has.
        _processRunner
            .AnyRun()
            .Throws(new ProcessTimeoutException("'jb' timed out after 30 seconds."));

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => LoggingLocator().LocateAsync(Ct));

        // Assert — the timeout clause follows the candidate's name in both the Tried list and the log line, so
        // the exception's own "'jb' timed out after ..." wording would name the executable twice over.
        exception.Message.ShouldBe(
            "JetBrains ReSharper CLI tools found, but no candidate reported a version.\n\n"
            + "Tried:\n"
            + "  jb: timed out after 30 seconds\n"
            + $"  {DotnetToolsCandidate}: timed out after 30 seconds\n\n"
            + "At least one candidate started and was killed after 30 seconds without reporting one, "
            + "so jb is installed and installing it again will not help.\n"
            + "A probe that slow is usually a machine busy at startup rather than a broken install, so retry the call.");
        ProbeLineFor("jb").Property("ProbeOutcome").ShouldBe("timed out after 30 seconds");
    }

    [Fact]
    public async Task LocateAsync_OneCandidateMissingAndAnotherTimesOut_StillReportsJbAsInstalled()
    {
        // Arrange — one candidate proving a jb exists is enough, however the others ended.
        Probe("jb").Throws(new Win32Exception("The system cannot find the file specified."));
        Probe(DotnetToolsCandidate).Throws(new ProcessTimeoutException("timed out"));
        JbLocator locator = Locator();

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => locator.LocateAsync(Ct));

        // Assert
        exception.Message.ShouldContain("jb is installed and installing it again will not help");
        exception.Message.ShouldContain("The system cannot find the file specified.");
        exception.Message.ShouldNotContain("dotnet tool install");
    }

    [Theory]
    [InlineData(1, "some jb error", "  jb: some jb error")]
    [InlineData(2, "", "  jb: exited with code 2")]
    public async Task LocateAsync_EveryProbeExitsNonZero_PointsAtTheProbeCommandAndReportsTheDetail(
        int exitCode, string standardError, string expectedDetail)
    {
        // Arrange — a jb that runs and fails is installed too, so it gets the other half of the same split.
        // What it wrote to standard error is the detail; for a candidate that wrote nothing, the exit code
        // is the whole of what can be said about it.
        _processRunner
            .AnyRun()
            .Returns(new ProcessResult(exitCode, string.Empty, standardError));
        JbLocator locator = Locator();

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => locator.LocateAsync(Ct));

        // Assert
        exception.Message.ShouldContain("jb is installed and installing it again will not help");
        exception.Message.ShouldContain("Run `jb inspectcode --version` yourself");
        exception.Message.ShouldContain(expectedDetail);
        exception.Message.ShouldNotContain("dotnet tool install");
    }

    [Fact]
    public async Task LocateAsync_CalledTwiceAfterSuccess_DoesNotReprobe()
    {
        // Arrange
        Probe("jb").Returns(Healthy);
        JbLocator locator = Locator();

        // Act
        await locator.LocateAsync(Ct);
        await locator.LocateAsync(Ct);

        // Assert
        await _processRunner.Received(1).AnyRunOf("jb");
    }

    [Fact]
    public async Task LocateAsync_ShimRewrittenSinceTheProbe_ProbesAgainAndReportsTheNewBuild()
    {
        // Arrange — an in-place `dotnet tool update -g`: it rewrites the shim, and the shim then starts the
        // new build. A server that kept its first answer would label every later run with a build that is
        // no longer installed.
        string shim = JbInstalls.PlantGlobalToolShim(_environment.HomeDirectory);
        Probe("jb").Throws(new Win32Exception("The system cannot find the file specified."));
        Probe(DotnetToolsCandidate).Returns(
            JbStubs.VersionProbeAnswerFor("2026.2.1"),
            JbStubs.VersionProbeAnswerFor("2026.2.3.1"));
        JbLocator locator = LoggingLocator();
        await locator.LocateAsync(Ct);

        // Act
        JbInstalls.UpdateInPlace(shim);
        JbInstallation installation = await locator.LocateAsync(Ct);

        // Assert — and the line saying why names the build the change replaced.
        installation.Version.ShouldBe("2026.2.3.1");
        await _processRunner.Received(2).AnyRunOf(DotnetToolsCandidate);
        LogEntry reprobe = _logs.WithProperty("JbVersion").ShouldHaveSingleItem();
        reprobe.Level.ShouldBe(LogLevel.Debug);
        reprobe.Property("JbVersion").ShouldBe("2026.2.1");
    }

    [Fact]
    public async Task LocateAsync_ShimUntouched_ReusesTheProbe()
    {
        // Arrange — the stat is what keeps the probe off every call: seconds of process start against the
        // read of one or two files.
        JbInstalls.PlantGlobalToolShim(_environment.HomeDirectory);
        Probe("jb").Throws(new Win32Exception("The system cannot find the file specified."));
        Probe(DotnetToolsCandidate).Returns(Healthy);
        JbLocator locator = Locator();

        // Act
        await locator.LocateAsync(Ct);
        await locator.LocateAsync(Ct);

        // Assert
        await _processRunner.Received(1).AnyRunOf(DotnetToolsCandidate);
    }

    [Fact]
    public async Task LocateAsync_JbOnPathRewrittenSinceTheProbe_ProbesAgain()
    {
        // Arrange — the first candidate is whatever jb comes first on PATH, which need not be the shim, and
        // a jb installed some other way can be updated in place too.
        string directory = _environment.CreateTempDirectory();
        string jb = JbInstalls.PlantJbIn(directory);
        _environment.SetVariable(PathSearch.PathVariable, directory);
        Probe("jb").Returns(Healthy);
        JbLocator locator = Locator();
        await locator.LocateAsync(Ct);

        // Act
        JbInstalls.UpdateInPlace(jb);
        await locator.LocateAsync(Ct);

        // Assert
        await _processRunner.Received(2).AnyRunOf("jb");
    }

    [Fact]
    public async Task LocateAsync_FirstCandidateFailsBeforeALaterOneSucceeds_LogsTheFailedCandidateAndItsCost()
    {
        // Arrange — the case nothing in the log could account for. A throw from the spawn escapes before
        // ProcessRunner writes either of its own lines, and a candidate that fails before a later one
        // succeeds never reaches the "No jb reported a version" summary, so its time was attributed to nothing.
        Probe("jb").Throws(new Win32Exception("The system cannot find the file specified."));
        Probe(DotnetToolsCandidate).Returns(Healthy);

        // Act
        await LoggingLocator().LocateAsync(Ct);

        // Assert
        LogEntry failed = ProbeLineFor("jb");
        failed.Level.ShouldBe(LogLevel.Debug);
        failed.Property("ProbeOutcome").ShouldBe("The system cannot find the file specified.");
        failed.Property("ElapsedMs").ShouldNotBeNull();
    }

    [Fact]
    public async Task LocateAsync_CandidateReportsAVersion_LogsThatCandidateToo()
    {
        // Arrange
        Probe("jb").Returns(Healthy);

        // Act
        await LoggingLocator().LocateAsync(Ct);

        // Assert — the candidate that ends the loop is a line as well, so the probe's whole cost adds up from
        // the log rather than being inferred from the failures alone.
        LogEntry succeeded = ProbeLineFor("jb");
        succeeded.Property("ProbeOutcome").ShouldBe("reported version 2026.1.2");
        succeeded.Property("ElapsedMs").ShouldNotBeNull();
    }

    [Fact]
    public async Task LocateAsync_CallerCancelsDuringAProbe_PropagatesRatherThanTryingTheNextCandidate()
    {
        // Arrange — cancellation is the one ending that is not a candidate's fault. Read as a failure it
        // would be logged as one and the loop would go on probing after the call the probe serves has gone.
        Probe("jb").Throws(new OperationCanceledException());
        Probe(DotnetToolsCandidate).Returns(Healthy);

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => LoggingLocator().LocateAsync(Ct));

        // Assert
        await _processRunner.DidNotReceive().AnyRunOf(DotnetToolsCandidate);
        _logs.WithProperty("Candidate").ShouldBeEmpty();
    }

    private Task<ProcessResult> Probe(string fileName)
    {
        return _processRunner.AnyRunOf(fileName);
    }

    private JbLocator Locator()
    {
        return new JbLocator(_processRunner, _environment, NullLogger<JbLocator>.Instance);
    }

    private JbLocator LoggingLocator()
    {
        return new JbLocator(_processRunner, _environment, Logs.Capturing(_logs).CreateLogger<JbLocator>());
    }

    /// <summary>The one probe line about <paramref name="candidate" /> — by property, never by prose.</summary>
    private LogEntry ProbeLineFor(string candidate)
    {
        List<LogEntry> lines = _logs
            .WithProperty("Candidate")
            .Where(entry => Equals(entry.Property("Candidate"), candidate))
            .ToList();

        return lines.ShouldHaveSingleItem();
    }
}