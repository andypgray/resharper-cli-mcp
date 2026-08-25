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
        Probe("jb").Returns(new ProcessResult(0, VersionOutput, string.Empty));
        JbLocator locator = new(_processRunner, _environment, NullLogger<JbLocator>.Instance);

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
        Probe(DotnetToolsCandidate).Returns(new ProcessResult(0, VersionOutput, string.Empty));
        JbLocator locator = new(_processRunner, _environment, NullLogger<JbLocator>.Instance);

        // Act
        JbInstallation installation = await locator.LocateAsync(Ct);

        // Assert
        installation.ExecutablePath.ShouldBe(DotnetToolsCandidate);
        installation.ExecutablePath.ShouldContain(Path.Combine(".dotnet", "tools"));
    }

    [Fact]
    public async Task LocateAsync_NoVersionLine_UsesTrimmedStdoutAsVersion()
    {
        // Arrange
        Probe("jb").Returns(new ProcessResult(0, "  ReSharper CLI build 12345  \n", string.Empty));
        JbLocator locator = new(_processRunner, _environment, NullLogger<JbLocator>.Instance);

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
            .RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, standardOutput!, string.Empty));
        JbLocator locator = new(_processRunner, _environment, NullLogger<JbLocator>.Instance);

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => locator.LocateAsync(Ct));

        // Assert
        exception.Message.ShouldStartWith("JetBrains ReSharper CLI tools not found.");
        exception.Message.ShouldContain("exited with code 0 but reported no version");
    }

    [Fact]
    public async Task LocateAsync_FirstCandidateReportsNoVersion_FallsBackToNextCandidate()
    {
        // Arrange
        Probe("jb").Returns(new ProcessResult(0, string.Empty, string.Empty));
        Probe(DotnetToolsCandidate).Returns(new ProcessResult(0, VersionOutput, string.Empty));
        JbLocator locator = new(_processRunner, _environment, NullLogger<JbLocator>.Instance);

        // Act
        JbInstallation installation = await locator.LocateAsync(Ct);

        // Assert
        installation.ExecutablePath.ShouldBe(DotnetToolsCandidate);
        installation.Version.ShouldBe("2026.1.2");
    }

    [Fact]
    public async Task LocateAsync_FirstCandidateExitsNonZero_FallsBackToNextCandidate()
    {
        // Arrange
        Probe("jb").Returns(new ProcessResult(1, string.Empty, "some jb error"));
        Probe(DotnetToolsCandidate).Returns(new ProcessResult(0, VersionOutput, string.Empty));
        JbLocator locator = new(_processRunner, _environment, NullLogger<JbLocator>.Instance);

        // Act
        JbInstallation installation = await locator.LocateAsync(Ct);

        // Assert
        installation.ExecutablePath.ShouldBe(DotnetToolsCandidate);
    }

    [Fact]
    public async Task LocateAsync_AllCandidatesFail_ThrowsWithInstallGuidanceNamingBothCandidates()
    {
        // Arrange
        _processRunner
            .RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Throws(new Win32Exception("The system cannot find the file specified."));
        JbLocator locator = new(_processRunner, _environment, NullLogger<JbLocator>.Instance);

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => locator.LocateAsync(Ct));

        // Assert
        exception.Message.ShouldStartWith("JetBrains ReSharper CLI tools not found.");
        exception.Message.ShouldContain("dotnet tool install JetBrains.ReSharper.GlobalTools -g");
        exception.Message.ShouldContain("jb:");
        exception.Message.ShouldContain(DotnetToolsCandidate);
    }

    [Fact]
    public async Task LocateAsync_CalledTwiceAfterSuccess_DoesNotReprobe()
    {
        // Arrange
        Probe("jb").Returns(new ProcessResult(0, VersionOutput, string.Empty));
        JbLocator locator = new(_processRunner, _environment, NullLogger<JbLocator>.Instance);

        // Act
        await locator.LocateAsync(Ct);
        await locator.LocateAsync(Ct);

        // Assert
        await _processRunner.Received(1).RunAsync(
            "jb", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LocateAsync_FirstCandidateFailsBeforeALaterOneSucceeds_LogsTheFailedCandidateAndItsCost()
    {
        // Arrange — the case nothing in the log could account for. A throw from the spawn escapes before
        // ProcessRunner writes either of its own lines, and a candidate that fails before a later one
        // succeeds never reaches the "No jb found" summary, so the time it spent was attributed to nothing.
        Probe("jb").Throws(new Win32Exception("The system cannot find the file specified."));
        Probe(DotnetToolsCandidate).Returns(new ProcessResult(0, VersionOutput, string.Empty));

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
        Probe("jb").Returns(new ProcessResult(0, VersionOutput, string.Empty));

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
        Probe(DotnetToolsCandidate).Returns(new ProcessResult(0, VersionOutput, string.Empty));

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => LoggingLocator().LocateAsync(Ct));

        // Assert
        await _processRunner.DidNotReceive().RunAsync(
            DotnetToolsCandidate, Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        _logs.WithProperty("Candidate").ShouldBeEmpty();
    }

    private Task<ProcessResult> Probe(string fileName)
    {
        return _processRunner.RunAsync(fileName, Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
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