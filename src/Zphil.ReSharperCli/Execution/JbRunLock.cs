using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Formatting;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     An exclusive, cross-process lock over one ReSharper cache generation, held for the duration of a
///     <c>jb</c> run.
/// </summary>
/// <remarks>
///     <para>
///         <c>jb</c> takes its own lock on the cache generation directory it opens, and a second <c>jb</c>
///         that cannot take it does not wait: it silently forks a new generation and starts from an
///         <em>empty</em> cache. Two sessions inspecting one solution therefore make each other slow —
///         the second run does the full cold analysis, usually past the run timeout — and leave a few
///         hundred megabytes of dead cache behind. Queueing on the warm generation is strictly better
///         than racing onto a cold one, so callers serialize here first.
///     </para>
///     <para>
///         A lock <em>file</em> rather than a named mutex: a mutex has thread affinity and so cannot be
///         held across the <c>await</c> on the run, while a file handle has none. The OS also drops the
///         handle when a holder crashes or is tree-killed, so a dead holder cannot deadlock the next
///         caller and there is no abandoned state to recover here.
///     </para>
///     <para>
///         What the dropped handle does <em>not</em> release is the <c>jb</c> that holder spawned. It keeps
///         running, and keeps the cache generation open, so the next caller reads a free lock, queues for no
///         time at all, and meets exactly the fork described above — the guarantee is void across an
///         ungraceful death unless the child dies with its server.
///         <see cref="ChildProcessLifetime" /> is what makes it, where the platform offers a primitive for
///         it, so that a released lock and a free generation mean the same thing again.
///     </para>
///     <para>
///         The lock is an optimisation, never a dependency: anything that goes wrong other than genuine
///         contention degrades to a weaker lock (or none) and lets the run proceed. The two speculative
///         entry points — <see cref="TryAcquire" /> and <see cref="TryAcquireByKeyAsync" /> — invert that
///         rule deliberately, for the reason given on the first of them.
///     </para>
/// </remarks>
/// <param name="maxWait">
///     How long a caller queues for a run in flight before giving up. The composition root resolves
///     <see cref="JbRunTimeout" /> once and wires the same value here and to the run cap in
///     <see cref="Services.JbRunner" />, so a queued call is bounded by wait + run and the two caps cannot
///     drift apart — including when <c>RESHARPER_MCP_TIMEOUT_SECS</c> moves them.
/// </param>
internal sealed class JbRunLock(TimeSpan maxWait, ILogger<JbRunLock> logger)
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    ///     How long a caller has to have queued for the wait to be worth an <c>Information</c> line rather
    ///     than a <c>Debug</c> one. An uncontended acquire is sub-millisecond, so anything past this is
    ///     another <c>jb</c> the caller sat behind — which is one of the three things that make a call slow,
    ///     and the only one nothing else in the log records.
    /// </summary>
    /// <remarks>
    ///     <c>JbRunProgressSnapshot.JustArrived</c> applies it for the same judgement rather than choosing a
    ///     threshold of its own: a progress message too has to decide whether a caller has genuinely queued
    ///     behind someone, and the answer should not be able to differ between the log and the message
    ///     describing the same wait.
    /// </remarks>
    internal static readonly TimeSpan NotableWait = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     One gate per lock key, so callers inside this process queue on a semaphore instead of polling
    ///     the file. Never evicted: it holds one small entry per (solution, cache home) pair the server
    ///     has ever run against, which is a handful for the life of a process.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>
    ///     The wait this lock enforces, for a caller that has to report the wait it is serving out.
    ///     Exposed rather than injected a second time so the number a queued caller is told about and the
    ///     number it is actually bounded by cannot drift apart.
    /// </summary>
    internal TimeSpan MaxWait { get; } = maxWait;

    /// <summary>
    ///     Wait for exclusive use of the cache generation behind <paramref name="solutionPath" /> and
    ///     <paramref name="cacheHome" />, then return the handle whose disposal releases it. Waiting is
    ///     capped across both layers of the lock; exceeding the cap throws a
    ///     <see cref="UserErrorException" /> naming the contention, because running anyway is the very
    ///     bug this exists to prevent.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(string solutionPath, string cacheHome, CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();

        string key;
        string lockFilePath;
        try
        {
            key = JbSidecar.ComputeKey(solutionPath, cacheHome);
            lockFilePath = LockFilePathFor(cacheHome, key);
        }
        catch (Exception exception) when (CannotDeriveLock(exception))
        {
            logger.LogWarning(exception, "Could not derive a jb run lock for solution {SolutionPath} in cache home {CacheHome}; running unserialized", solutionPath, cacheHome);
            return new Holder(null, null, logger);
        }

        SemaphoreSlim gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(Remaining(waited), cancellationToken).ConfigureAwait(false))
            throw Contended(solutionPath);

        try
        {
            FileStream? file = TryPrepareCacheHome(cacheHome)
                ? await OpenExclusiveAsync(lockFilePath, solutionPath, waited, cancellationToken).ConfigureAwait(false)
                : null;

            ReportAcquisition(solutionPath, waited.Elapsed, file is not null);

            return new Holder(gate, file, logger);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    /// <summary>
    ///     Say how long this caller queued, and whether the lock it got is the cross-process one. The wait
    ///     is the whole point: nothing else records that a call spent four minutes behind another session's
    ///     <c>jb</c>, and read from the outside that call is indistinguishable from a slow one.
    /// </summary>
    private void ReportAcquisition(string solutionPath, TimeSpan waited, bool crossProcess)
    {
        string scope = crossProcess ? "cross-process" : "in-process only";

        if (waited >= NotableWait)
        {
            logger.LogInformation(
                "Queued {WaitedMs} ms for the ReSharper cache generation of {SolutionPath} before another jb run released it ({LockScope})",
                (long)waited.TotalMilliseconds,
                solutionPath,
                scope);

            return;
        }

        logger.LogDebug(
            "Took the ReSharper cache generation of {SolutionPath} after {WaitedMs} ms ({LockScope})",
            solutionPath,
            (long)waited.TotalMilliseconds,
            scope);
    }

    /// <summary>
    ///     Take exclusive use of the cache generation without waiting for it, returning the handle whose
    ///     disposal releases it, or <see langword="null" /> when it could not be taken outright. For
    ///     speculative work only — a caller that must run uses <see cref="AcquireAsync" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Synchronous because every step of a zero-wait acquire is: an <c>async</c> signature here would
    ///         have nothing to await, and would promise a wait that must never happen.
    ///     </para>
    ///     <para>
    ///         This inverts the degradation rule the rest of the class follows. <see cref="AcquireAsync" />
    ///         falls back to a weaker lock and runs anyway, because a call the user asked for must not fail
    ///         over a missing optimisation. Here the calculus reverses: a <em>background</em> run that cannot
    ///         prove exclusivity and starts regardless causes exactly the cold-cache fork this lock exists to
    ///         prevent, for nobody's benefit. So every degradation — an underivable key, an unusable cache
    ///         home, a lock file that will not open for any reason at all — returns <see langword="null" />
    ///         and the speculative run never starts, which is the one ending it can report as having cost
    ///         nothing.
    ///     </para>
    /// </remarks>
    public IDisposable? TryAcquire(string solutionPath, string cacheHome)
    {
        string key;
        string lockFilePath;
        try
        {
            key = JbSidecar.ComputeKey(solutionPath, cacheHome);
            lockFilePath = LockFilePathFor(cacheHome, key);
        }
        catch (Exception exception) when (CannotDeriveLock(exception))
        {
            return null;
        }

        SemaphoreSlim gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0)) return null;

        FileStream? file;
        try
        {
            file = TryPrepareCacheHome(cacheHome) ? TryOpenExclusiveOnce(lockFilePath) : null;
        }
        catch
        {
            gate.Release();
            throw;
        }

        return HolderOrRelease(gate, file, logger);
    }

    /// <summary>
    ///     Take exclusive use of the cache generation behind a lock key, waiting no longer than
    ///     <paramref name="patience" /> for it, or <see langword="null" /> when it could not be taken. For a
    ///     caller holding another generation's lease already, which is why the wait is a small explicit
    ///     budget rather than the run cap: the outer lease is held throughout, so this one must be short and
    ///     must always end.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Keyed rather than pathed because the caller has no solution path to key from. The keys are the
    ///         cache home's own sidecar file names, so a generation belonging to a solution this process has
    ///         never resolved — another checkout, analysed by another session — can still be locked before it
    ///         is read from. <see cref="JbSidecar.ComputeKey" /> produces the same key from the pair, so the
    ///         two entry points contend with each other exactly as they should.
    ///     </para>
    ///     <para>
    ///         Follows <see cref="TryAcquire" />'s inverted degradation rule for the same reason: this serves
    ///         speculative work, and every failure — an unusable path, a cache home that will not open, a
    ///         holder that outlasts the patience — answers <see langword="null" /> so the caller steps aside.
    ///         The bounded wait plus skip-on-failure is also what makes taking it <em>inside</em> another
    ///         lease safe: nested acquisition cannot deadlock when the inner wait always ends and failing to
    ///         get it is an ordinary outcome.
    ///     </para>
    /// </remarks>
    public async Task<IDisposable?> TryAcquireByKeyAsync(
        string cacheHome,
        string key,
        TimeSpan patience,
        CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();

        string lockFilePath;
        try
        {
            lockFilePath = LockFilePathFor(cacheHome, key);
        }
        catch (Exception exception) when (CannotDeriveLock(exception))
        {
            return null;
        }

        SemaphoreSlim gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(Remaining(patience, waited), cancellationToken).ConfigureAwait(false)) return null;

        FileStream? file;
        try
        {
            file = TryPrepareCacheHome(cacheHome)
                ? await TryOpenExclusiveWithin(lockFilePath, patience, waited, cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch
        {
            gate.Release();
            throw;
        }

        return HolderOrRelease(gate, file, logger);
    }

    /// <summary>
    ///     Where the lock file for <paramref name="key" /> lives: beside the warm marker and the cold
    ///     tombstone, under <see cref="JbSidecar" />'s one naming scheme for all three.
    /// </summary>
    internal static string LockFilePathFor(string cacheHome, string key)
    {
        return JbSidecar.PathForKey(cacheHome, key, "lock");
    }

    /// <summary>
    ///     The one definition of "the lock cannot even be derived": what <see cref="JbSidecar" />'s path
    ///     derivations throw for an argument no path API will accept. Every entry point filters on this and
    ///     then degrades its own way — a set edited in one prologue and not the others would silently change
    ///     which acquisition shapes serialize.
    /// </summary>
    private static bool CannotDeriveLock(Exception exception)
    {
        return exception is ArgumentException or NotSupportedException or PathTooLongException;
    }

    /// <summary>
    ///     The speculative acquires' shared tail. Unlike <see cref="AcquireAsync" />, "could not prove
    ///     exclusivity" is a <em>return</em> rather than a throw, so releasing in a catch is not enough:
    ///     leaving the gate taken on the null path would wedge every later caller of this generation —
    ///     foreground ones included — for the life of the process.
    /// </summary>
    private static IDisposable? HolderOrRelease(SemaphoreSlim gate, FileStream? file, ILogger logger)
    {
        if (file is null)
        {
            gate.Release();
            return null;
        }

        return new Holder(gate, file, logger);
    }

    /// <summary>
    ///     Open the lock file exclusively, retrying while another holder has it, or <see langword="null" />
    ///     when the file cannot be used at all (no write permission, say) — the caller then keeps only the
    ///     in-process half of the lock rather than failing a run over a missing optimisation.
    /// </summary>
    private async Task<FileStream?> OpenExclusiveAsync(
        string lockFilePath,
        string solutionPath,
        Stopwatch waited,
        CancellationToken cancellationToken)
    {
        while (true)
            try
            {
                return OpenLockFile(lockFilePath);
            }
            catch (IOException exception) when (IsContention(exception) && waited.Elapsed < MaxWait)
            {
                await Task.Delay(Shorter(RetryInterval, Remaining(waited)), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception) when (IsContention(exception))
            {
                throw Contended(solutionPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                logger.LogWarning(exception, "Could not take the jb run lock file {LockFilePath}; concurrent runs against this solution will not be serialized", lockFilePath);
                return null;
            }
    }

    /// <summary>
    ///     Open the lock file exclusively, retrying while another holder has it until
    ///     <paramref name="patience" /> is spent, then <see langword="null" />. The difference from
    ///     <see cref="OpenExclusiveAsync" /> is what happens at the cap: this returns rather than throwing,
    ///     because its caller has somewhere to go without the lock and a foreground run does not.
    /// </summary>
    private static async Task<FileStream?> TryOpenExclusiveWithin(
        string lockFilePath,
        TimeSpan patience,
        Stopwatch waited,
        CancellationToken cancellationToken)
    {
        while (true)
            try
            {
                return OpenLockFile(lockFilePath);
            }
            catch (IOException exception) when (IsContention(exception) && waited.Elapsed < patience)
            {
                await Task.Delay(Shorter(RetryInterval, Remaining(patience, waited)), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return null;
            }
    }

    /// <summary>
    ///     One attempt at the lock file and no retry: for <see cref="TryAcquire" />, contention and a
    ///     permanently unusable path are the same answer — do not run. Nothing is logged, because a
    ///     speculative run stepping aside is the design working, not a fault.
    /// </summary>
    private static FileStream? TryOpenExclusiveOnce(string lockFilePath)
    {
        try
        {
            return OpenLockFile(lockFilePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The one spelling of the exclusive open. <see cref="FileShare.None" /> is the lock itself. Not
    ///     DeleteOnClose: a zero-byte file left behind is cheaper than the delete-pending race it would
    ///     introduce between a releasing holder and an arriving one.
    /// </summary>
    private static FileStream OpenLockFile(string lockFilePath)
    {
        return new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>
    ///     Create the cache home so the lock file has somewhere to live (jb would create it anyway),
    ///     reporting whether the file lock can be attempted at all.
    /// </summary>
    private bool TryPrepareCacheHome(string cacheHome)
    {
        try
        {
            Directory.CreateDirectory(cacheHome);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogWarning(exception, "Could not prepare cache home {CacheHome} for the jb run lock; concurrent runs against this solution will not be serialized", cacheHome);
            return false;
        }
    }

    /// <summary>
    ///     A failure to open the lock file means another holder has it, unless the path itself is the
    ///     problem — those cases are permanent and must degrade rather than burn the whole wait budget.
    /// </summary>
    private static bool IsContention(IOException exception)
    {
        return exception is not (DirectoryNotFoundException or FileNotFoundException or PathTooLongException);
    }

    private UserErrorException Contended(string solutionPath)
    {
        return new UserErrorException(
            $"Another jb run already holds the ReSharper cache for \"{solutionPath}\" and did not finish within {DurationFormatter.Format(MaxWait)}.\n"
            + "Work against one solution's cache is serialized on purpose: a second concurrent jb cannot share the warm cache, so it silently forks a new empty one and both runs get slower — and a reset deleting a generation mid-run would take it out from under whoever was using it.\n"
            + "A speculative pre-warm inside this server always yields, so the holder is another session: its inspect, its cleanup, or its own pre-warm. Retry once that run has finished.");
    }

    private TimeSpan Remaining(Stopwatch waited)
    {
        return Remaining(MaxWait, waited);
    }

    private static TimeSpan Remaining(TimeSpan budget, Stopwatch waited)
    {
        TimeSpan remaining = budget - waited.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static TimeSpan Shorter(TimeSpan first, TimeSpan second)
    {
        return first < second ? first : second;
    }

    /// <summary>
    ///     Releases whichever layers were actually taken, innermost first, and only once — a double
    ///     dispose must not over-release the semaphore and let a second caller in.
    /// </summary>
    private sealed class Holder(SemaphoreSlim? gate, FileStream? file, ILogger logger) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try
            {
                file?.Dispose();
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Failed to release the jb run lock file cleanly");
            }

            gate?.Release();
        }
    }
}