using System.ComponentModel;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;

namespace Zphil.ReSharperCli.Tests.Discovery;

public sealed class JbLocatorTests : IDisposable
{
    private const string VersionOutput =
        "JetBrains Inspect Code 2026.1.2\nRunning on x64 OS in x64 architecture\nVersion: 2026.1.2\n";

    private readonly FakeEnvironment _environment = new();

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
        JbLocator locator = new(_processRunner, _environment);

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
        JbLocator locator = new(_processRunner, _environment);

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
        JbLocator locator = new(_processRunner, _environment);

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
        JbLocator locator = new(_processRunner, _environment);

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
        JbLocator locator = new(_processRunner, _environment);

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
        JbLocator locator = new(_processRunner, _environment);

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
        JbLocator locator = new(_processRunner, _environment);

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
        JbLocator locator = new(_processRunner, _environment);

        // Act
        await locator.LocateAsync(Ct);
        await locator.LocateAsync(Ct);

        // Assert
        await _processRunner.Received(1).RunAsync(
            "jb", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    private Task<ProcessResult> Probe(string fileName)
    {
        return _processRunner.RunAsync(fileName, Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }
}