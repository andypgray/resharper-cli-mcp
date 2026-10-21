using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Formatting;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Provides an exclusive, cross-process lock over one ReSharper cache generation, held for the duration of
///     a <c>jb</c> run.
/// </summary>
/// <remarks>
///     <para>
///         <c>jb</c> takes its own lock on the cache generation directory it opens, and a second <c>jb</c>
///         that cannot take it does not wait: it silently forks a new generation and starts from an empty
///         cache. Two runs against one solution therefore make each other slow, the second doing the full cold
///         analysis, and leave a dead generation behind. Queueing on the warm generation is better than racing
///         onto a cold one, so callers serialize here first, waiting up to <paramref name="maxWait" /> for a run
///         in flight.
///     </para>
///     <para>
///         A lock file rather than a named mutex: a mutex has thread affinity and so cannot be held across the
///         <c>await</c> on the run, while a file handle has none. The OS also drops the handle when a holder
///         crashes or is tree-killed, so a dead holder cannot deadlock the next caller and there is no abandoned
///         state to recover here.
///     </para>
///     <para>
///         The dropped handle does not release the <c>jb</c> its holder spawned, which keeps the generation
///         open, so a free lock means a free generation only where <see cref="ChildProcessLifetime" /> binds
///         the child to its server.
///     </para>
///     <para>
///         What it partitions is a directory, not the machine: two runs against different solutions pass this
///         lock uncontended, which is <see cref="JbRunSlot" />'s to bound.
///     </para>
///     <para>
///         The lock is an optimisation, never a dependency: anything that goes wrong other than contention
///         degrades to a weaker lock, or none, and lets the run proceed. The speculative entry points,
///         <see cref="TryAcquire" /> and <see cref="TryAcquireByKeyAsync" />, invert that rule, for the reason
///         given on <see cref="TryAcquire" />.
///     </para>
/// </remarks>
internal sealed class JbRunLock(TimeSpan maxWait, ILogger<JbRunLock> logger)
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    ///     How long a caller has to have queued for the wait to be worth an <c>Information</c> line rather
    ///     than a <c>Debug</c> one.
    /// </summary>
    /// <remarks>
    ///     An uncontended acquire is sub-millisecond, so anything past this is another <c>jb</c> the caller sat
    ///     behind, which nothing else in the log records. Every judgement of whether a caller queued behind
    ///     someone uses this threshold, so the log and a progress message describing one wait cannot disagree.
    /// </remarks>
    internal static readonly TimeSpan NotableWait = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     One gate per lock key, so callers inside this process queue on a semaphore instead of polling the
    ///     file. Never evicted: it holds one small entry per lock key this process has asked for, which stays a
    ///     handful for the life of a process.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>
    ///     Gets the longest a caller queues before <see cref="AcquireAsync" /> gives up.
    /// </summary>
    /// <remarks>
    ///     A caller that reports the wait it is serving out reads the bound here, so the number it names is the
    ///     one it is under.
    /// </remarks>
    internal TimeSpan MaxWait { get; } = maxWait;

    /// <summary>
    ///     Waits for exclusive use of the cache generation behind <paramref name="solutionPath" /> and
    ///     <paramref name="cacheHome" />, then returns the handle whose disposal releases it.
    /// </summary>
    /// <remarks>
    ///     Waiting is capped across both layers of the lock; exceeding the cap throws a
    ///     <see cref="UserErrorException" /> naming the contention, because running anyway is the bug this
    ///     exists to prevent.
    /// </remarks>
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
            return Held(null, null, logger);
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

            return Held(gate, file, logger);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    /// <summary>
    ///     Logs how long this caller queued, and whether the lock it got is the cross-process one.
    /// </summary>
    /// <remarks>
    ///     Nothing else records that a call spent minutes behind another session's <c>jb</c>, which from outside
    ///     looks like a slow run.
    /// </remarks>
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
    ///     Takes exclusive use of the cache generation without waiting for it, returning the handle whose
    ///     disposal releases it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Answers <see langword="null" /> when it could not be taken outright. For speculative work only; a
    ///         caller that must run uses <see cref="AcquireAsync" />.
    ///     </para>
    ///     <para>
    ///         Synchronous because every step of a zero-wait acquire is: an <c>async</c> signature here would
    ///         have nothing to await, and would promise a wait that must never happen.
    ///     </para>
    ///     <para>
    ///         This inverts the class's degradation rule. <see cref="AcquireAsync" /> falls back to a weaker lock
    ///         and runs anyway, because a call the user asked for must not fail over a missing optimisation. A
    ///         background run that cannot prove exclusivity and starts regardless causes the cold-cache fork this
    ///         lock exists to prevent, for nobody's benefit. So every degradation, whether an underivable key, an
    ///         unusable cache home, or a lock file that will not open for any reason, returns
    ///         <see langword="null" />, and the speculative run does not start.
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
    ///     Takes exclusive use of the cache generation behind a lock key, waiting no longer than
    ///     <paramref name="patience" /> for it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Answers <see langword="null" /> when it could not be taken. For use while another generation's
    ///         lease is already held, which is why the wait is a small explicit budget rather than the run cap:
    ///         the outer lease is held throughout, so this one must be short and must always end.
    ///     </para>
    ///     <para>
    ///         Keyed rather than pathed, for a generation whose solution this process has never resolved, such
    ///         as another checkout's: the keys are the cache home's own sidecar file names, so that generation
    ///         can be locked before it is read from. <see cref="JbSidecar.ComputeKey" /> derives the same key
    ///         from a solution path and cache home, so the keyed and pathed entry points contend with each other.
    ///     </para>
    ///     <para>
    ///         Follows <see cref="TryAcquire" />'s inverted degradation rule: every failure, including a holder
    ///         that outlasts <paramref name="patience" />, answers <see langword="null" /> so the caller steps
    ///         aside. That bounded, skippable wait is what makes taking this lock inside another lease safe:
    ///         nested acquisition cannot deadlock when the inner wait always ends and failing to get it is an
    ///         ordinary outcome.
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
    ///     Returns the path of the lock file for <paramref name="key" />, beside the cache home's other sidecar
    ///     files.
    /// </summary>
    internal static string LockFilePathFor(string cacheHome, string key)
    {
        return JbSidecar.PathForKey(cacheHome, key, "lock");
    }

    /// <summary>
    ///     Determines whether <paramref name="exception" /> is one that <see cref="JbSidecar" />'s path
    ///     derivations throw for an argument no path API will accept, meaning the lock cannot be derived at all.
    /// </summary>
    /// <remarks>
    ///     Every entry point filters on this one predicate and then degrades its own way, so the set of
    ///     exceptions that means "no lock" cannot differ between acquisition shapes.
    /// </remarks>
    private static bool CannotDeriveLock(Exception exception)
    {
        return exception is ArgumentException or NotSupportedException or PathTooLongException;
    }

    /// <summary>
    ///     Returns the held lock, or releases the gate and returns <see langword="null" /> when the lock file
    ///     could not be taken.
    /// </summary>
    /// <remarks>
    ///     Releasing in a catch is not enough here, because "could not prove exclusivity" is a return rather
    ///     than a throw: leaving the gate taken on the null path would wedge every later caller of this
    ///     generation, foreground ones included, for the life of the process.
    /// </remarks>
    private static IDisposable? HolderOrRelease(SemaphoreSlim gate, FileStream? file, ILogger logger)
    {
        if (file is null)
        {
            gate.Release();
            return null;
        }

        return Held(gate, file, logger);
    }

    /// <summary>
    ///     Opens the lock file exclusively, retrying while another holder has it.
    /// </summary>
    /// <remarks>
    ///     Answers <see langword="null" /> when the file cannot be used at all, such as without write
    ///     permission; the caller then keeps only the in-process half of the lock rather than failing a run over
    ///     a missing optimisation.
    /// </remarks>
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
    ///     Opens the lock file exclusively, retrying while another holder has it until
    ///     <paramref name="patience" /> is spent, then answers <see langword="null" />.
    /// </summary>
    /// <remarks>
    ///     The difference from <see cref="OpenExclusiveAsync" /> is what happens at the cap: this returns rather
    ///     than throwing, because its caller has somewhere to go without the lock and a foreground run does not.
    /// </remarks>
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
    ///     Makes one attempt at the lock file, with no retry, answering <see langword="null" /> on contention and
    ///     on an unusable path alike.
    /// </summary>
    /// <remarks>
    ///     Nothing is logged, because a speculative run stepping aside is the design working, not a fault.
    /// </remarks>
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
    ///     Opens the lock file exclusively: <see cref="FileShare.None" /> is the lock itself.
    /// </summary>
    /// <remarks>
    ///     Not DeleteOnClose: a zero-byte file left behind is cheaper than the delete-pending race it would
    ///     introduce between a releasing holder and an arriving one.
    /// </remarks>
    private static FileStream OpenLockFile(string lockFilePath)
    {
        return new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>
    ///     Creates the cache home so the lock file has somewhere to live (jb would create it anyway),
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
    ///     Determines whether a failure to open the lock file means another holder has it, rather than a problem
    ///     with the path itself.
    /// </summary>
    /// <remarks>
    ///     A path problem is permanent, so it has to degrade at once rather than burn the whole wait budget.
    /// </remarks>
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
    ///     Returns the handle that releases whichever layers were taken, innermost first, and only once: a
    ///     double dispose must not over-release the semaphore and let a second caller in.
    /// </summary>
    private static IDisposable Held(SemaphoreSlim? gate, FileStream? file, ILogger logger)
    {
        return new ReleaseOnce(() =>
        {
            try
            {
                file?.Dispose();
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Failed to release the jb run lock file cleanly");
            }

            gate?.Release();
        });
    }
}