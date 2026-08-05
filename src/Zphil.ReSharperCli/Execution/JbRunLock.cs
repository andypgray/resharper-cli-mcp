using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Serilog;

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
///         caller and there is no abandoned state to recover.
///     </para>
///     <para>
///         The lock is an optimisation, never a dependency: anything that goes wrong other than genuine
///         contention degrades to a weaker lock (or none) and lets the run proceed.
///     </para>
/// </remarks>
internal sealed class JbRunLock(TimeSpan? maxWait = null)
{
    /// <summary>
    ///     How long a caller queues for a run in flight before giving up. Symmetric with the run cap in
    ///     <see cref="Services.JbRunner" />, so a queued call is bounded by wait + run.
    /// </summary>
    private static readonly TimeSpan DefaultMaxWait = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    ///     One gate per lock key, so callers inside this process queue on a semaphore instead of polling
    ///     the file. Never evicted: it holds one small entry per (solution, cache home) pair the server
    ///     has ever run against, which is a handful for the life of a process.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    private readonly TimeSpan _maxWait = maxWait ?? DefaultMaxWait;

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
            key = ComputeKey(solutionPath, cacheHome);
            lockFilePath = LockFilePathFor(cacheHome, key);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Log.Warning(exception, "Could not derive a jb run lock for solution {SolutionPath} in cache home {CacheHome}; running unserialized", solutionPath, cacheHome);
            return new Holder(null, null);
        }

        SemaphoreSlim gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(Remaining(waited), cancellationToken).ConfigureAwait(false))
            throw Contended(solutionPath);

        try
        {
            FileStream? file = TryPrepareCacheHome(cacheHome)
                ? await OpenExclusiveAsync(lockFilePath, solutionPath, waited, cancellationToken).ConfigureAwait(false)
                : null;

            return new Holder(gate, file);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    /// <summary>
    ///     Identifies one cache generation: a short hash of the normalised (solution, cache home) pair.
    ///     Both paths are absolute by contract — <c>ResolvedConfig</c> resolves them — so normalising here
    ///     only folds separators and Windows casing, and never consults the process working directory.
    /// </summary>
    internal static string ComputeKey(string solutionPath, string cacheHome)
    {
        var material = $"{Normalize(solutionPath)}\n{Normalize(cacheHome)}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>
    ///     Where the lock file for <paramref name="key" /> lives: inside the cache home itself, and
    ///     deliberately not in the temp directory — the cache home <em>is</em> the contended resource, so
    ///     two sessions sharing a cache share a lock file even when their temp directories differ.
    /// </summary>
    internal static string LockFilePathFor(string cacheHome, string key)
    {
        return Path.Combine(Path.GetFullPath(cacheHome), $".resharper-cli-mcp-{key}.lock");
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
                // Not DeleteOnClose: a zero-byte file left behind is cheaper than the delete-pending race
                // it would introduce between a releasing holder and an arriving one.
                return new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when (IsContention(exception) && waited.Elapsed < _maxWait)
            {
                await Task.Delay(Shorter(RetryInterval, Remaining(waited)), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception) when (IsContention(exception))
            {
                throw Contended(solutionPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Log.Warning(exception, "Could not take the jb run lock file {LockFilePath}; concurrent runs against this solution will not be serialized", lockFilePath);
                return null;
            }
    }

    /// <summary>
    ///     Create the cache home so the lock file has somewhere to live (jb would create it anyway),
    ///     reporting whether the file lock can be attempted at all.
    /// </summary>
    private static bool TryPrepareCacheHome(string cacheHome)
    {
        try
        {
            Directory.CreateDirectory(cacheHome);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Warning(exception, "Could not prepare cache home {CacheHome} for the jb run lock; concurrent runs against this solution will not be serialized", cacheHome);
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
            $"Another inspect or cleanup is already running against \"{solutionPath}\" and did not finish within {ProcessRunner.FormatDuration(_maxWait)}.\n"
            + "Runs against one solution are serialized on purpose: a second concurrent jb cannot share the warm ReSharper cache, so it silently forks a new empty one and both runs get slower.\n"
            + "Retry once the run in flight has finished.");
    }

    private TimeSpan Remaining(Stopwatch waited)
    {
        TimeSpan remaining = _maxWait - waited.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static TimeSpan Shorter(TimeSpan first, TimeSpan second)
    {
        return first < second ? first : second;
    }

    private static string Normalize(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    /// <summary>
    ///     Releases whichever layers were actually taken, innermost first, and only once — a double
    ///     dispose must not over-release the semaphore and let a second caller in.
    /// </summary>
    private sealed class Holder(SemaphoreSlim? gate, FileStream? file) : IDisposable
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
                Log.Warning(exception, "Failed to release the jb run lock file cleanly");
            }

            gate?.Release();
        }
    }
}