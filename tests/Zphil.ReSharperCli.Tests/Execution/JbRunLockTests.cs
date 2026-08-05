using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     <see cref="JbRunLock" /> exists so two sessions inspecting one solution queue on the warm ReSharper
///     cache instead of forking a second, empty one. Both halves are covered: the in-process semaphore that
///     keeps concurrent calls inside one server ordered, and the lock <em>file</em> that does the same
///     across server processes — the case that actually bites, since two sessions are two processes. The
///     cross-process half is stood in for by these tests opening the lock file themselves with
///     <see cref="FileShare.None" />, which is exactly what another holder's handle looks like to the OS.
/// </summary>
public sealed class JbRunLockTests : IDisposable
{
    private const string SolutionPath = "/repo/App.sln";

    /// <summary>A wait cap short enough that a test can hit it, standing in for the 5-minute default.</summary>
    private static readonly TimeSpan ShortWait = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private readonly string _cacheHome;
    private readonly FakeEnvironment _environment = new();

    public JbRunLockTests()
    {
        _cacheHome = _environment.CreateTempDirectory();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_SecondCallerInSameProcess_WaitsUntilTheFirstReleases()
    {
        // Arrange
        JbRunLock runLock = new();
        IDisposable first = await runLock.AcquireAsync(SolutionPath, _cacheHome, Ct);

        // Act
        var second = runLock.AcquireAsync(SolutionPath, _cacheHome, Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        bool completedWhileHeld = second.IsCompleted;
        first.Dispose();

        // Assert — it was still queued behind the holder, and gets in once the holder releases.
        completedWhileHeld.ShouldBeFalse();
        (await second.WaitAsync(Generous, Ct)).Dispose();
    }

    [Fact]
    public async Task AcquireAsync_LockFileHeldByAnotherProcess_WaitsUntilThatHandleCloses()
    {
        // Arrange — a handle taken outside this JbRunLock instance, standing in for another server process.
        // Without the file half of the lock this test fails: the in-process semaphore is uncontended here.
        JbRunLock runLock = new();
        FileStream otherProcess = OpenLockFileExclusively();

        // Act
        var queued = runLock.AcquireAsync(SolutionPath, _cacheHome, Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(400), Ct);
        bool completedWhileHeld = queued.IsCompleted;
        await otherProcess.DisposeAsync();

        // Assert
        completedWhileHeld.ShouldBeFalse();
        (await queued.WaitAsync(Generous, Ct)).Dispose();
    }

    [Fact]
    public async Task AcquireAsync_DifferentSolutionsInOneCacheHome_DoNotBlockEachOther()
    {
        // Arrange — the lock is per cache generation, not per server: unrelated solutions must run at once.
        JbRunLock runLock = new(ShortWait);
        using IDisposable first = await runLock.AcquireAsync("/repo/One.sln", _cacheHome, Ct);
        var waited = Stopwatch.StartNew();

        // Act
        using IDisposable second = await runLock.AcquireAsync("/repo/Two.sln", _cacheHome, Ct);

        // Assert — no wait at all; had they shared a lock, this would have thrown at the cap instead.
        waited.Elapsed.ShouldBeLessThan(ShortWait);
    }

    [Fact]
    public async Task AcquireAsync_OneSolutionAcrossDifferentCacheHomes_DoNotBlockEachOther()
    {
        // Arrange — the same solution analysed into two cache homes has two generations, so no contention.
        JbRunLock runLock = new(ShortWait);
        string otherCacheHome = _environment.CreateTempDirectory();
        using IDisposable first = await runLock.AcquireAsync(SolutionPath, _cacheHome, Ct);
        var waited = Stopwatch.StartNew();

        // Act
        using IDisposable second = await runLock.AcquireAsync(SolutionPath, otherCacheHome, Ct);

        // Assert
        waited.Elapsed.ShouldBeLessThan(ShortWait);
    }

    [Fact]
    public async Task AcquireAsync_WaitCapExceeded_ThrowsUserErrorNamingTheSolution()
    {
        // Arrange — a holder that never releases.
        JbRunLock runLock = new(ShortWait);
        await using FileStream otherProcess = OpenLockFileExclusively();

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => runLock.AcquireAsync(SolutionPath, _cacheHome, Ct));

        // Assert — running anyway is the bug, so the caller is told what is in flight and to retry.
        exception.Message.ShouldContain(SolutionPath);
        exception.Message.ShouldContain("already running");
        exception.Message.ShouldContain("Retry");
    }

    [Fact]
    public async Task AcquireAsync_CacheHomeCannotBeCreated_DegradesInsteadOfFailingTheRun()
    {
        // Arrange — a *file* where the cache home should be, so the directory can never be created. The lock
        // is an optimisation, never a dependency: the jb run must still go ahead without it.
        JbRunLock runLock = new(ShortWait);
        string blocked = Path.Combine(_environment.CreateTempDirectory(), "not-a-directory");
        await File.WriteAllTextAsync(blocked, string.Empty, Ct);

        // Act
        IDisposable degraded = await runLock.AcquireAsync(SolutionPath, blocked, Ct).WaitAsync(Generous, Ct);
        degraded.Dispose();

        // Assert — and the degraded handle still releases, so it cannot wedge the next call either.
        var waited = Stopwatch.StartNew();
        using IDisposable next = await runLock.AcquireAsync(SolutionPath, blocked, Ct);
        waited.Elapsed.ShouldBeLessThan(ShortWait);
    }

    [Fact]
    public async Task AcquireAsync_LockFilePathUnusable_DegradesInsteadOfFailingTheRun()
    {
        // Arrange — a *directory* sitting where the lock file goes: the cache home is fine, so the run gets
        // that far, but the file can never be opened. Still not a reason to fail a jb run.
        JbRunLock runLock = new(ShortWait);
        Directory.CreateDirectory(LockFilePath());
        var waited = Stopwatch.StartNew();

        // Act
        using IDisposable acquired = await runLock.AcquireAsync(SolutionPath, _cacheHome, Ct);

        // Assert — it degraded at once rather than retrying to the cap.
        waited.Elapsed.ShouldBeLessThan(ShortWait);
    }

    [Fact]
    public async Task AcquireAsync_CacheHomeIsNotAValidPath_DegradesInsteadOfFailingTheRun()
    {
        // Arrange — a cache home no path API will accept, so even the lock's key cannot be derived.
        JbRunLock runLock = new(ShortWait);
        string invalid = _cacheHome + "\0invalid";

        // Act
        using IDisposable acquired = await runLock.AcquireAsync(SolutionPath, invalid, Ct);

        // Assert — the whole lock is skipped; jb gets to run and decide for itself.
        Directory.Exists(_cacheHome).ShouldBeTrue();
    }

    [Fact]
    public async Task AcquireAsync_PreviousHolderDiedWithoutReleasing_DoesNotDeadlock()
    {
        // Arrange — a raw handle abandoned the way a crashed or tree-killed process abandons one: no orderly
        // release, no cleanup. This is why the lock is a file rather than a named mutex, which would instead
        // leave the next caller an AbandonedMutexException to recover from.
        JbRunLock runLock = new(ShortWait);
        SafeFileHandle crashed = File.OpenHandle(LockFilePath(), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        crashed.Dispose();
        var waited = Stopwatch.StartNew();

        // Act
        using IDisposable acquired = await runLock.AcquireAsync(SolutionPath, _cacheHome, Ct);

        // Assert — no wait, and the leftover zero-byte lock file is harmless.
        waited.Elapsed.ShouldBeLessThan(ShortWait);
        File.Exists(LockFilePath()).ShouldBeTrue();
    }

    [Fact]
    public async Task Dispose_CalledTwice_DoesNotAdmitAnExtraCaller()
    {
        // Arrange — an over-release would let a second jb onto the cache while the first still holds it.
        JbRunLock runLock = new(ShortWait);
        IDisposable first = await runLock.AcquireAsync(SolutionPath, _cacheHome, Ct);
        first.Dispose();
        first.Dispose();

        // Act
        using IDisposable second = await runLock.AcquireAsync(SolutionPath, _cacheHome, Ct);
        var third = runLock.AcquireAsync(SolutionPath, _cacheHome, Ct);

        // Assert — the third caller queues behind the live holder and gives up at the cap.
        await Should.ThrowAsync<UserErrorException>(() => third);
    }

    [Fact]
    public async Task TryAcquire_LockFileHeldByAnotherProcess_SkipsWithoutWaiting()
    {
        // Arrange — the *default* five-minute cap on purpose. If the zero-wait were a shorter promise rather
        // than structural, this test would hang instead of failing, which is the distinction worth pinning.
        JbRunLock runLock = new();
        await using FileStream otherProcess = OpenLockFileExclusively();
        var waited = Stopwatch.StartNew();

        // Act
        IDisposable? lease = runLock.TryAcquire(SolutionPath, _cacheHome);

        // Assert
        lease.ShouldBeNull();
        waited.Elapsed.ShouldBeLessThan(ShortWait);
    }

    [Fact]
    public async Task TryAcquire_WhileACallerInThisProcessHoldsTheLease_SkipsWithoutWaiting()
    {
        // Arrange — the in-process half: the speculative caller never queues behind a real one.
        JbRunLock runLock = new();
        using IDisposable foreground = await runLock.AcquireAsync(SolutionPath, _cacheHome, Ct);
        var waited = Stopwatch.StartNew();

        // Act
        IDisposable? lease = runLock.TryAcquire(SolutionPath, _cacheHome);

        // Assert
        lease.ShouldBeNull();
        waited.Elapsed.ShouldBeLessThan(ShortWait);
    }

    [Fact]
    public void TryAcquire_Granted_HoldsBothHalvesUntilDisposed()
    {
        // Arrange
        JbRunLock runLock = new();

        // Act
        IDisposable? lease = runLock.TryAcquire(SolutionPath, _cacheHome);

        // Assert — a lease that did not really take the *file* would let another server process onto the
        // cache generation, which is the whole failure this lock exists to prevent.
        lease.ShouldNotBeNull();
        Should.Throw<IOException>(() => OpenLockFileExclusively().Dispose());
        lease.Dispose();
        OpenLockFileExclusively().Dispose();
    }

    [Fact]
    public async Task TryAcquire_OnceDisposed_ReadmitsAForegroundCaller()
    {
        // Arrange
        JbRunLock runLock = new(ShortWait);
        IDisposable? lease = runLock.TryAcquire(SolutionPath, _cacheHome);
        lease.ShouldNotBeNull();
        lease.Dispose();
        var waited = Stopwatch.StartNew();

        // Act
        using IDisposable foreground = await runLock.AcquireAsync(SolutionPath, _cacheHome, Ct);

        // Assert
        waited.Elapsed.ShouldBeLessThan(ShortWait);
    }

    [Fact]
    public async Task TryAcquire_CacheHomeCannotBeCreated_SkipsInsteadOfDegrading()
    {
        // Arrange — a *file* where the cache home should be. AcquireAsync degrades here and runs anyway,
        // because a call the user asked for outranks the optimisation; a speculative run has no such claim,
        // and running it unserialized would fork the cold cache the lock exists to prevent.
        JbRunLock runLock = new(ShortWait);
        string blocked = Path.Combine(_environment.CreateTempDirectory(), "not-a-directory");
        await File.WriteAllTextAsync(blocked, string.Empty, Ct);

        // Act
        IDisposable? lease = runLock.TryAcquire(SolutionPath, blocked);

        // Assert — and the gate it took on the way to that decision is handed back.
        lease.ShouldBeNull();
        using IDisposable foreground = await runLock.AcquireAsync(SolutionPath, blocked, Ct).WaitAsync(Generous, Ct);
    }

    [Fact]
    public void TryAcquire_LockFilePathUnusable_SkipsInsteadOfDegrading()
    {
        // Arrange — a *directory* sitting where the lock file goes: the cache home is fine, the file can
        // never be opened.
        JbRunLock runLock = new(ShortWait);
        Directory.CreateDirectory(LockFilePath());

        // Act & Assert
        runLock.TryAcquire(SolutionPath, _cacheHome).ShouldBeNull();
    }

    [Fact]
    public void TryAcquire_CacheHomeIsNotAValidPath_SkipsInsteadOfDegrading()
    {
        // Arrange — a cache home no path API will accept, so even the lock's key cannot be derived.
        JbRunLock runLock = new(ShortWait);

        // Act & Assert
        runLock.TryAcquire(SolutionPath, _cacheHome + "\0invalid").ShouldBeNull();
    }

    [Fact]
    public async Task TryAcquire_ThatCouldNotProveExclusivity_StillLetsTheNextCallerIn()
    {
        // Arrange — the gate-leak guard. TryAcquire takes the in-process semaphore, then discovers it cannot
        // open the lock file and *returns* null rather than throwing, so AcquireAsync's release-on-throw does
        // not cover it. A leak here would be paid by a real call: it would queue against nothing, burn the
        // whole wait cap, and fail with a contention error naming a run that never existed.
        JbRunLock runLock = new(ShortWait);
        Directory.CreateDirectory(LockFilePath());
        runLock.TryAcquire(SolutionPath, _cacheHome).ShouldBeNull();
        var waited = Stopwatch.StartNew();

        // Act
        using IDisposable foreground = await runLock.AcquireAsync(SolutionPath, _cacheHome, Ct);

        // Assert
        waited.Elapsed.ShouldBeLessThan(ShortWait);
    }

    [Fact]
    public void ComputeKey_FoldsPathsThatNameTheSameCacheGeneration()
    {
        // Assert — a trailing separator must not fork one generation's lock into two...
        JbRunLock.ComputeKey(SolutionPath, _cacheHome + Path.DirectorySeparatorChar)
            .ShouldBe(JbRunLock.ComputeKey(SolutionPath, _cacheHome));

        // ...while a different solution in the same cache home is a different generation.
        JbRunLock.ComputeKey("/repo/Other.sln", _cacheHome)
            .ShouldNotBe(JbRunLock.ComputeKey(SolutionPath, _cacheHome));
    }

    private string LockFilePath()
    {
        return JbRunLock.LockFilePathFor(_cacheHome, JbRunLock.ComputeKey(SolutionPath, _cacheHome));
    }

    /// <summary>Hold the lock file the way another server process would: exclusively, until disposed.</summary>
    private FileStream OpenLockFileExclusively()
    {
        return new FileStream(LockFilePath(), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
}