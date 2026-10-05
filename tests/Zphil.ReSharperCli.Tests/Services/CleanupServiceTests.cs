using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Services;

/// <summary>
///     Pins <see cref="CleanupService" /> through its structured <see cref="CleanupOutcome" />, over real files
///     planted under a per-instance temp directory so the parallel run stays race-free. The fake
///     <see cref="IProcessRunner" /> never touches files, so a test that needs a <c>Changed</c> (or a
///     deleted-file <c>StatusUnknown</c>) drives the mutation from a side-effecting jb stub.
/// </summary>
public sealed class CleanupServiceTests : IDisposable
{
    private readonly ResolvedConfig _config;
    private readonly FakeEnvironment _environment = new();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly CleanupService _service;
    private readonly string _solutionDirectory;

    public CleanupServiceTests()
    {
        _solutionDirectory = _environment.CurrentDirectory;
        string solutionPath = _environment.PlantSolution("App.sln");

        // The cache home is a real directory: JbRunLock creates it and takes its lock file there, so a
        // literal like "/cache" would leave a stray folder at the drive root.
        _config = Configs.Bare(solutionPath, _environment.CreateTempDirectory());
        _service = new CleanupService(JbRunners.Create(_processRunner), NullLogger<CleanupService>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public async Task RunAsync_FileRewritten_ClassifiesChanged()
    {
        // Arrange
        string path = SolutionFiles.Plant(_solutionDirectory, "src/A.cs", "original");
        StubJbRunning(() => File.WriteAllText(path, "cleaned up"));

        // Act
        CleanupOutcome outcome = await _service.RunAsync(_config, ["src/A.cs"], CleanupService.DefaultProfile, Ct);

        // Assert
        outcome.Profile.ShouldBe(CleanupService.DefaultProfile);
        CleanupEntry entry = outcome.Entries.ShouldHaveSingleItem();
        entry.Display.ShouldBe("src/A.cs");
        entry.Status.ShouldBe(CleanupFileStatus.Changed);
    }

    [Fact]
    public async Task RunAsync_RewrittenWithIdenticalBytes_ClassifiesUnchanged()
    {
        // Arrange — jb re-writes the file with byte-identical content (a new mtime, same bytes). Content
        // hashing must call this Unchanged; a (length, mtime) heuristic would wrongly report Changed.
        string path = SolutionFiles.Plant(_solutionDirectory, "src/A.cs", "same bytes");
        StubJbRunning(() => File.WriteAllText(path, "same bytes"));

        // Act
        CleanupOutcome outcome = await _service.RunAsync(_config, ["src/A.cs"], CleanupService.DefaultProfile, Ct);

        // Assert
        outcome.Entries.ShouldHaveSingleItem().Status.ShouldBe(CleanupFileStatus.Unchanged);
    }

    [Fact]
    public async Task RunAsync_AfterReadFails_ClassifiesStatusUnknownWithoutThrowing()
    {
        // Arrange — jb deletes the file (exit 0), so the after-hash read fails. The run already succeeded, so
        // the outcome must classify it StatusUnknown rather than letting the hash failure throw.
        string path = SolutionFiles.Plant(_solutionDirectory, "src/A.cs", "content");
        StubJbRunning(() => File.Delete(path));

        // Act
        CleanupOutcome outcome = await _service.RunAsync(_config, ["src/A.cs"], CleanupService.DefaultProfile, Ct);

        // Assert
        outcome.Entries.ShouldHaveSingleItem().Status.ShouldBe(CleanupFileStatus.StatusUnknown);
    }

    [Fact]
    public async Task RunAsync_MixedConcreteAndWildcard_ClassifiesEachInOrder()
    {
        // Arrange
        string path = SolutionFiles.Plant(_solutionDirectory, "src/A.cs", "before");
        StubJbRunning(() => File.WriteAllText(path, "after"));

        // Act
        CleanupOutcome outcome = await _service.RunAsync(
            _config, ["src/A.cs", "src/**/*.cs"], CleanupService.DefaultProfile, Ct);

        // Assert — request order preserved; the wildcard is Pattern (outside the hashed count).
        outcome.Entries.Count.ShouldBe(2);
        outcome.Entries[0].Status.ShouldBe(CleanupFileStatus.Changed);
        outcome.Entries[1].Display.ShouldBe("src/**/*.cs");
        outcome.Entries[1].Status.ShouldBe(CleanupFileStatus.Pattern);
    }

    [Fact]
    public async Task RunAsync_WildcardEntry_SkipsValidationAndClassifiesPattern()
    {
        // Arrange — a wildcard is handed to jb unvalidated even though nothing matches it on disk.
        StubExit(0);

        // Act
        CleanupOutcome outcome = await _service.RunAsync(_config, ["src/**/*.cs"], CleanupService.DefaultProfile, Ct);

        // Assert
        outcome.Entries.ShouldHaveSingleItem().Status.ShouldBe(CleanupFileStatus.Pattern);
        await _processRunner.Received(1).AnyRunOf("jb");
    }

    [Fact]
    public async Task RunAsync_AbsoluteExistingPathUntouched_ClassifiesUnchanged()
    {
        // Arrange
        string absolute = SolutionFiles.Plant(_solutionDirectory, "src/Real.cs", "x");
        StubExit(0);

        // Act
        CleanupOutcome outcome = await _service.RunAsync(_config, [absolute], CleanupService.DefaultProfile, Ct);

        // Assert
        CleanupEntry entry = outcome.Entries.ShouldHaveSingleItem();
        entry.Display.ShouldBe(absolute);
        entry.Status.ShouldBe(CleanupFileStatus.Unchanged);
    }

    [Fact]
    public async Task RunAsync_AbsolutePath_ReachesJbAsARelativeIncludePattern()
    {
        // Arrange — through the service that builds the argument. jb's --include takes relative paths only,
        // so an absolute one is an Ant pattern matched against the solution model that can never hit.
        string absolute = SolutionFiles.Plant(_solutionDirectory, "src/A.cs", "x");
        List<string>? arguments = null;
        _processRunner
            .AnyRunOf("jb")
            .Returns(call =>
            {
                arguments = [.. call.Arguments()];
                return JbStubs.Success;
            });

        // Act
        await _service.RunAsync(_config, [absolute], CleanupService.DefaultProfile, Ct);

        // Assert
        arguments.ShouldNotBeNull();
        arguments.ShouldContain("--include=src/A.cs");
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_ThrowsUserErrorSurfacingStderr()
    {
        // Arrange — a non-zero exit throws before any classification.
        SolutionFiles.Plant(_solutionDirectory, "A.cs", "x");
        _processRunner
            .AnyRunOf("jb")
            .Returns(new ProcessResult(1, string.Empty, "Unknown profile 'No Such Profile'"));

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _service.RunAsync(_config, ["A.cs"], "No Such Profile", Ct));

        // Assert
        exception.Message.ShouldContain("Unknown profile 'No Such Profile'");
    }

    [Fact]
    public async Task RunAsync_JbMatchedNothing_SaysTheWholePassFailedAndNamesThePatterns()
    {
        // Arrange — jb's own signal for this reads as a success to an agent that has just made a batch of
        // edits: "No items were found to cleanup" is the whole of the stderr, which is enough to have the pass
        // skipped. The framing is cleanup's to give, since only cleanup knows N files were named.
        SolutionFiles.Plant(_solutionDirectory, "src/A.cs", "x");
        SolutionFiles.Plant(_solutionDirectory, "src/B.cs", "x");
        _processRunner
            .AnyRunOf("jb")
            .Returns(new ProcessResult(3, string.Empty, "No items were found to cleanup"));

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _service.RunAsync(_config, ["src/A.cs", "src/B.cs"], CleanupService.DefaultProfile, Ct));

        // Assert — the contradiction is stated outright, the patterns jb was actually given are listed, and
        // the one remaining cause of a matched-nothing run is named.
        exception.Message.ShouldStartWith("jb cleanupcode exited with code 3. No file was cleaned up");
        exception.Message.ShouldContain("not as \"nothing needed changing\"");
        exception.Message.ShouldContain("jb reported: No items were found to cleanup");
        exception.Message.ShouldContain("The 2 --include pattern(s) it was given:");
        exception.Message.ShouldContain("  - src/A.cs");
        exception.Message.ShouldContain("  - src/B.cs");
        exception.Message.ShouldContain("on disk but in no project matches nothing");
    }

    [Fact]
    public async Task RunAsync_AbsolutePathAndJbMatchedNothing_ListsTheTranslatedPattern()
    {
        // Arrange — the report echoes the caller's own spelling, so the failure message is the one place the
        // translated form is visible. That is what makes "these are the patterns jb was given" true.
        string absolute = SolutionFiles.Plant(_solutionDirectory, "src/A.cs", "x");
        _processRunner
            .AnyRunOf("jb")
            .Returns(new ProcessResult(3, string.Empty, "No items were found to cleanup"));

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _service.RunAsync(_config, [absolute], CleanupService.DefaultProfile, Ct));

        // Assert
        exception.Message.ShouldContain("The 1 --include pattern(s) it was given:\n  - src/A.cs");
        exception.Message.ShouldNotContain(absolute);
    }

    [Fact]
    public async Task RunAsync_NonZeroExitWithNoStandardError_OmitsTheQuotedLineRatherThanLeavingItEmpty()
    {
        // Arrange — jb does not always say why. A bare "jb reported:" with nothing after it reads as output
        // that went missing, so the line is only there when there is something to quote.
        SolutionFiles.Plant(_solutionDirectory, "src/A.cs", "x");
        StubExit(9);

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _service.RunAsync(_config, ["src/A.cs"], CleanupService.DefaultProfile, Ct));

        // Assert
        exception.Message.ShouldNotContain("jb reported:");
        exception.Message.ShouldContain("The 1 --include pattern(s) it was given:\n  - src/A.cs");
    }

    [Fact]
    public async Task RunAsync_RunHitTheCap_IsNotReframedAsAFailedPass()
    {
        // Arrange — the discriminator the typed exception exists for. A cleanup killed at the cap may already
        // have rewritten files, so it must not be told nothing was cleaned up, and the runner's message names
        // the variable that moves the cap.
        SolutionFiles.Plant(_solutionDirectory, "src/A.cs", "x");
        _processRunner
            .AnyRunOf("jb")
            .ThrowsAsync(new ProcessTimeoutException("'jb' timed out."));

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _service.RunAsync(_config, ["src/A.cs"], CleanupService.DefaultProfile, Ct));

        // Assert
        exception.Message.ShouldStartWith("jb cleanupcode timed out after");
        exception.Message.ShouldNotContain("No file was cleaned up");
    }

    [Fact]
    public async Task RunAsync_MissingPlainFile_ThrowsNamingItAndDoesNotInvokeJb()
    {
        // Arrange — no file planted, so the concrete path does not exist; validation runs before hashing.

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _service.RunAsync(_config, ["src/Missing.cs"], CleanupService.DefaultProfile, Ct));

        // Assert
        exception.Message.ShouldContain("src/Missing.cs");
        await _processRunner.DidNotReceive().AnyRun();
    }

    private void StubExit(int exitCode)
    {
        _processRunner
            .AnyRunOf("jb")
            .Returns(new ProcessResult(exitCode, string.Empty, string.Empty));
    }

    /// <summary>Stubs jb to run <paramref name="duringRun" /> (a filesystem side effect) then exit 0.</summary>
    private void StubJbRunning(Action duringRun)
    {
        _processRunner
            .AnyRunOf("jb")
            .Returns(_ =>
            {
                duringRun();
                return JbStubs.Success;
            });
    }
}