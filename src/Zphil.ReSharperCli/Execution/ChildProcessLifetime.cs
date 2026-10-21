using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     Represents what a spawn runs, and whether a platform wrapper stands between it and the command the
///     caller asked for.
/// </summary>
internal readonly record struct SpawnCommand(string FileName, IReadOnlyList<string> Arguments, bool Wrapped)
{
    /// <summary>Returns the caller's command, unwrapped.</summary>
    internal static SpawnCommand AsRequested(string fileName, IReadOnlyList<string> arguments)
    {
        return new SpawnCommand(fileName, arguments, false);
    }
}

/// <summary>
///     Applies the strongest primitive each platform offers to stop a <c>jb</c> outliving the server that
///     started it.
/// </summary>
/// <remarks>
///     <para>
///         A <c>jb</c> that outlives its server keeps its cache generation open after the server's
///         <see cref="JbRunLock" /> handle is gone, so the next server reads a free lock, spawns <c>jb</c>, and
///         that <c>jb</c> forks a new empty generation rather than waiting.
///         <see cref="Services.CacheWarmer.StopAsync" /> covers an orderly shutdown by draining; nothing
///         in-process can cover <c>TerminateProcess</c>, which is why this reaches for an OS primitive that
///         outranks the process.
///     </para>
///     <para>
///         Windows gets a job object with <c>KILL_ON_JOB_CLOSE</c>, which covers <c>jb</c> and every
///         descendant. Linux gets <c>setpriv --pdeathsig SIGKILL</c>, which covers <c>jb</c> itself and not
///         what it forks. macOS gets nothing, and behaves as it would without this class.
///         <see cref="Guarantee" /> names the one in force so it can be reported, because a mechanism that
///         shows up only in its own absence is one nobody can confirm is working.
///     </para>
///     <para>
///         Every failure here leaves a child unguarded and nothing worse: a job that will not create, a
///         <c>setpriv</c> that is absent or too old, an assignment the kernel refuses. Nothing about a run
///         depends on this having worked.
///     </para>
///     <para>
///         A concrete class rather than a seam: it sits behind <see cref="IProcessRunner" />, which tests fake,
///         and what it does is observable only through a real OS process.
///     </para>
/// </remarks>
internal sealed class ChildProcessLifetime : IDisposable
{
    /// <summary>The Windows guarantee: the kernel kills the whole job when this process's handle to it closes.</summary>
    internal const string KillOnJobClose = "kill-on-job-close";

    /// <summary>The Linux guarantee: the kernel signals <c>jb</c> when the thread that forked it goes.</summary>
    internal const string ParentDeathSignalled = "parent-death-signal";

    /// <summary>No primitive, and so no guarantee — the value macOS always reports.</summary>
    internal const string NoGuarantee = "none";

    private readonly IEnvironment _environment;
    private readonly WindowsJobObject? _job;
    private readonly ILogger<ChildProcessLifetime> _logger;

    private readonly string? _setprivPath;

    /// <summary>
    ///     Guards <see cref="_spawns" /> so the pinned thread is created exactly once, however many spawns
    ///     race to be the first.
    /// </summary>
    private readonly Lock _spawnGate = new();

    private BlockingCollection<Action>? _spawns;

    public ChildProcessLifetime(IEnvironment environment, ILogger<ChildProcessLifetime> logger)
    {
        _environment = environment;
        _logger = logger;

        if (OperatingSystem.IsWindows())
        {
            _job = TryCreateJob(logger);
            Guarantee = _job is null ? NoGuarantee : KillOnJobClose;

            return;
        }

        if (OperatingSystem.IsLinux())
        {
            _setprivPath = ParentDeathSignal.TryLocate(environment.GetVariable(PathSearch.PathVariable));
            Guarantee = _setprivPath is null ? NoGuarantee : ParentDeathSignalled;

            return;
        }

        Guarantee = NoGuarantee;
    }

    /// <summary>
    ///     Gets the primitive in force for this process: <see cref="KillOnJobClose" />,
    ///     <see cref="ParentDeathSignalled" />, or <see cref="NoGuarantee" />.
    /// </summary>
    internal string Guarantee { get; }

    /// <summary>
    ///     Closes the job handle, which is what terminates anything still assigned to it.
    /// </summary>
    public void Dispose()
    {
        _job?.Dispose();
    }

    /// <summary>
    ///     Returns the command to spawn for <paramref name="fileName" />: on Linux the <c>setpriv</c> wrapper,
    ///     and elsewhere the caller's own command unchanged.
    /// </summary>
    internal SpawnCommand Rewrite(string fileName, IReadOnlyList<string> arguments)
    {
        if (!OperatingSystem.IsLinux() || _setprivPath is null) return SpawnCommand.AsRequested(fileName, arguments);

        // Resolved here rather than left to setpriv: a command that does not resolve is not wrapped, so it
        // fails as it does unwrapped, with a Win32Exception from this process rather than a setpriv exec error.
        string? target = PathSearch.Resolve(fileName, _environment.GetVariable(PathSearch.PathVariable));

        return ParentDeathSignal.Wrap(_setprivPath, target, fileName, arguments);
    }

    /// <summary>
    ///     Starts <paramref name="process" /> and binds its lifetime to this one.
    /// </summary>
    /// <remarks>
    ///     A missing executable throws <see cref="Win32Exception" /> from here as <see cref="Process.Start()" />
    ///     does, wrapped or not.
    /// </remarks>
    /// <param name="process">The process to start, built from the command <see cref="Rewrite" /> produced.</param>
    /// <param name="wrapped">
    ///     <see cref="SpawnCommand.Wrapped" /> from that same command. When <see cref="ParentDeathSignal.Wrap" />
    ///     declines, only the command knows, so re-deriving this from the platform and <c>setpriv</c> would
    ///     report a signal this child never got.
    /// </param>
    internal void Start(Process process, bool wrapped)
    {
        if (wrapped)
        {
            StartOnPinnedThread(process);
            Report(process, ParentDeathSignalled);

            return;
        }

        process.Start();

        if (_job is null)
        {
            Report(process, NoGuarantee);

            return;
        }

        // Immediately after the start and before anything else touches the child, so the unbound window is
        // as short as this API can make it.
        Report(process, _job.TryAssign(process) ? KillOnJobClose : NoGuarantee);
    }

    /// <summary>
    ///     Creates the job, or returns <see langword="null" /> after logging one warning.
    /// </summary>
    /// <remarks>
    ///     Creating a job takes no quota or privilege, so a failure is a surprise worth one warning, but never
    ///     worth failing the server over, since the feature is a backstop.
    /// </remarks>
    private static WindowsJobObject? TryCreateJob(ILogger<ChildProcessLifetime> logger)
    {
        try
        {
            return WindowsJobObject.Create();
        }
        catch (Exception exception) when (exception is Win32Exception or PlatformNotSupportedException)
        {
            logger.LogWarning(exception, "Could not create the job object that kills a jb left behind by an ungraceful shutdown; jb runs will behave as they did before");

            return null;
        }
    }

    /// <summary>Starts the process on a thread that outlives every run.</summary>
    /// <remarks>
    ///     <c>PR_SET_PDEATHSIG</c> fires when the thread that forked the child exits, not when the process does,
    ///     and <see cref="Process.Start()" /> forks on the calling thread, which under <c>async</c> is a
    ///     thread-pool thread free to retire mid-run and <c>SIGKILL</c> a healthy <c>jb</c>. One background
    ///     thread, created on first use and never exited, makes the signal fire only when the server goes.
    /// </remarks>
    private void StartOnPinnedThread(Process process)
    {
        BlockingCollection<Action> spawns = SpawnQueue();
        TaskCompletionSource spawned = new(TaskCreationOptions.RunContinuationsAsynchronously);

        spawns.Add(() =>
        {
            try
            {
                process.Start();
                spawned.SetResult();
            }
            catch (Exception exception)
            {
                spawned.SetException(exception);
            }
        });

        // Blocking on purpose: Process.Start is synchronous wherever it runs, and Start is written around a
        // spawn that has either happened or thrown by the time it returns. A faulted task rethrows here with
        // its original type and stack, so a missing executable still surfaces as a Win32Exception.
        spawned.Task.GetAwaiter().GetResult();
    }

    private BlockingCollection<Action> SpawnQueue()
    {
        lock (_spawnGate)
        {
            if (_spawns is not null) return _spawns;

            BlockingCollection<Action> spawns = new();
            Thread thread = new(() =>
            {
                foreach (Action spawn in spawns.GetConsumingEnumerable()) spawn();
            })
            {
                IsBackground = true,
                Name = "jb-spawner"
            };

            thread.Start();
            _spawns = spawns;

            return spawns;
        }
    }

    /// <summary>Logs what this child got, not what the platform offers.</summary>
    /// <remarks>
    ///     An assignment the kernel refused leaves an unbound child in a process whose startup line names a
    ///     guarantee, and this line is the only record of the difference.
    /// </remarks>
    private void Report(Process process, string guarantee)
    {
        _logger.LogDebug(
            "Started {ChildFileName} as pid {ChildProcessId}, orphan guard {OrphanGuard}",
            process.StartInfo.FileName,
            process.Id,
            guarantee);
    }
}