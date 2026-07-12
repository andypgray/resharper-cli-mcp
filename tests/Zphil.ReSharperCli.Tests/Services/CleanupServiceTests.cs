using NSubstitute;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;

namespace Zphil.ReSharperCli.Tests.Services;

/// <summary>
///     <see cref="CleanupService" /> mutates files in place, validates concrete (non-wildcard) paths before
///     invoking jb, and hashes each concrete file before and after the run to classify it. These tests plant
///     real files under a per-instance temp directory (so the parallel run stays race-free) and assert on the
///     structured <see cref="CleanupOutcome" /> — a far more robust contract than the old rendered string. The
///     fake <see cref="IProcessRunner" /> never touches files, so a test that needs a <c>Changed</c> (or a
///     deleted-file <c>StatusUnknown</c>) drives the mutation from a side-effecting jb stub.
/// </summary>
public sealed class CleanupServiceTests : IDisposable
{
    private readonly ResolvedConfig _config;
    private readonly FakeEnvironment _environment = new();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly string _solutionDirectory;

    public CleanupServiceTests()
    {
        _solutionDirectory = _environment.CurrentDirectory;
        string solutionPath = Path.Combine(_solutionDirectory, "App.sln");
        File.WriteAllText(solutionPath, string.Empty);
        _config = new ResolvedConfig(solutionPath, null, "/cache", null, null, "jb");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public async Task RunAsync_FileRewritten_ClassifiesChanged()
    {
        // Arrange — jb "cleans up" the file by writing different bytes during its run.
        string path = PlantFile("src/A.cs", "original");
        StubJbRunning(() => File.WriteAllText(path, "cleaned up"));
        CleanupService service = new(_processRunner);

        // Act
        CleanupOutcome outcome = await service.RunAsync(_config, ["src/A.cs"], CleanupService.DefaultProfile, Ct);

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
        string path = PlantFile("src/A.cs", "same bytes");
        StubJbRunning(() => File.WriteAllText(path, "same bytes"));
        CleanupService service = new(_processRunner);

        // Act
        CleanupOutcome outcome = await service.RunAsync(_config, ["src/A.cs"], CleanupService.DefaultProfile, Ct);

        // Assert
        outcome.Entries.ShouldHaveSingleItem().Status.ShouldBe(CleanupFileStatus.Unchanged);
    }

    [Fact]
    public async Task RunAsync_AfterReadFails_ClassifiesStatusUnknownWithoutThrowing()
    {
        // Arrange — jb deletes the file (exit 0), so the after-hash read fails. The run already succeeded, so
        // the outcome must classify it StatusUnknown rather than letting the hash failure throw.
        string path = PlantFile("src/A.cs", "content");
        StubJbRunning(() => File.Delete(path));
        CleanupService service = new(_processRunner);

        // Act
        CleanupOutcome outcome = await service.RunAsync(_config, ["src/A.cs"], CleanupService.DefaultProfile, Ct);

        // Assert
        outcome.Entries.ShouldHaveSingleItem().Status.ShouldBe(CleanupFileStatus.StatusUnknown);
    }

    [Fact]
    public async Task RunAsync_MixedConcreteAndWildcard_ClassifiesEachInOrder()
    {
        // Arrange — one concrete file jb rewrites, plus a wildcard that stays a Pattern (never a single file).
        string path = PlantFile("src/A.cs", "before");
        StubJbRunning(() => File.WriteAllText(path, "after"));
        CleanupService service = new(_processRunner);

        // Act
        CleanupOutcome outcome = await service.RunAsync(
            _config, ["src/A.cs", "src/**/*.cs"], CleanupService.DefaultProfile, Ct);

        // Assert — request order preserved; the wildcard is Pattern (excluded from the concrete denominator).
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
        CleanupService service = new(_processRunner);

        // Act
        CleanupOutcome outcome = await service.RunAsync(_config, ["src/**/*.cs"], CleanupService.DefaultProfile, Ct);

        // Assert
        outcome.Entries.ShouldHaveSingleItem().Status.ShouldBe(CleanupFileStatus.Pattern);
        await _processRunner.Received(1).RunAsync(
            "jb", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_AbsoluteExistingPathUntouched_ClassifiesUnchanged()
    {
        // Arrange — an absolute path jb does not modify.
        string absolute = PlantFile("src/Real.cs", "x");
        StubExit(0);
        CleanupService service = new(_processRunner);

        // Act
        CleanupOutcome outcome = await service.RunAsync(_config, [absolute], CleanupService.DefaultProfile, Ct);

        // Assert
        CleanupEntry entry = outcome.Entries.ShouldHaveSingleItem();
        entry.Display.ShouldBe(absolute);
        entry.Status.ShouldBe(CleanupFileStatus.Unchanged);
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_ThrowsUserErrorSurfacingStderr()
    {
        // Arrange — a non-zero exit throws before any classification.
        PlantFile("A.cs", "x");
        _processRunner
            .RunAsync("jb", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(1, string.Empty, "Unknown profile 'No Such Profile'"));
        CleanupService service = new(_processRunner);

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => service.RunAsync(_config, ["A.cs"], "No Such Profile", Ct));

        // Assert
        exception.Message.ShouldContain("Unknown profile 'No Such Profile'");
    }

    [Fact]
    public async Task RunAsync_MissingPlainFile_ThrowsNamingItAndDoesNotInvokeJb()
    {
        // Arrange — no file planted, so the concrete path does not exist; validation runs before hashing.
        CleanupService service = new(_processRunner);

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => service.RunAsync(_config, ["src/Missing.cs"], CleanupService.DefaultProfile, Ct));

        // Assert
        exception.Message.ShouldContain("src/Missing.cs");
        await _processRunner.DidNotReceive().RunAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    private string PlantFile(string relativePath, string content = "")
    {
        string fullPath = Path.Combine(_solutionDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    private void StubExit(int exitCode)
    {
        _processRunner
            .RunAsync("jb", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(exitCode, string.Empty, string.Empty));
    }

    /// <summary>Stub jb to run <paramref name="duringRun" /> (a filesystem side effect) then exit 0.</summary>
    private void StubJbRunning(Action duringRun)
    {
        _processRunner
            .RunAsync("jb", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                duringRun();
                return new ProcessResult(0, string.Empty, string.Empty);
            });
    }
}