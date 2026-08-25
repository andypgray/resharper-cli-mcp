using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     What a spawn actually runs, and whether a platform wrapper stands between it and what the caller
///     asked for. The flag is the wrap decision itself, carried to <see cref="ChildProcessLifetime.Start" />
///     so the spawn path and the guarantee it reports come from the same place the command did.
/// </summary>
internal readonly record struct SpawnCommand(string FileName, IReadOnlyList<string> Arguments, bool Wrapped)
{
    /// <summary>The caller's command, unwrapped.</summary>
    internal static SpawnCommand AsRequested(string fileName, IReadOnlyList<string> arguments)
    {
        return new SpawnCommand(fileName, arguments, false);
    }
}

/// <summary>
///     One concept — a <c>jb</c> this server started cannot outlive it — and the platform switch over the
///     strongest primitive each platform offers for it.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="JbRunLock" /> guarantees one <c>jb</c> at a time per cache generation, and the OS
///         releases its file handle when the holder dies. The <c>jb</c> that holder spawned is <em>not</em>
///         released: it keeps running, keeps ReSharper's own generation directory open, and the next server
///         then reads a free lock, spawns <c>jb</c>, and <c>jb</c> forks a new empty generation rather than
///         waiting. So the lock's guarantee is only as good as the child dying with its server.
///         <see cref="Services.CacheWarmer.StopAsync" /> covers the orderly path by draining; nothing
///         in-process can cover <c>TerminateProcess</c>, which is why this reaches for an OS primitive that
///         outranks the process instead.
///     </para>
///     <para>
///         Windows gets a job object with <c>KILL_ON_JOB_CLOSE</c>, which covers <c>jb</c> and every
///         descendant. Linux gets <c>setpriv --pdeathsig SIGKILL</c>, which covers <c>jb</c> itself and not
///         what it forks. macOS gets nothing, and keeps today's behaviour. <see cref="Guarantee" /> names
///         which is in force and rides on the startup fingerprint, because a mechanism that only shows up in
///         its own absence is one nobody can confirm is working.
///     </para>
///     <para>
///         Every failure here is silent and inert: a job that will not create, a <c>setpriv</c> that is
///         absent or too old, an assignment the kernel refuses. Each leaves exactly today's behaviour and
///         costs nothing, so nothing about a run depends on this having worked.
///     </para>
///     <para>
///         A concrete singleton rather than a third seam. It sits behind <see cref="IProcessRunner" />, which
///         is the seam tests already fake, and what it does is only observable through a real OS process —
///         so an interface over it would have nothing to stand in for.
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

    /// <summary>
    ///     Where the Linux half looks for <c>setpriv</c> and for the command it is about to wrap. Read through
    ///     <see cref="IEnvironment" /> like every other variable, so no test has to touch the real one.
    /// </summary>
    private const string PathVariable = "PATH";

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
            _setprivPath = ParentDeathSignal.TryLocate(environment.GetVariable(PathVariable));
            Guarantee = _setprivPath is null ? NoGuarantee : ParentDeathSignalled;

            return;
        }

        Guarantee = NoGuarantee;
    }

    /// <summary>
    ///     The primitive in force for this process: <see cref="KillOnJobClose" />,
    ///     <see cref="ParentDeathSignalled" />, or <see cref="NoGuarantee" />.
    /// </summary>
    internal string Guarantee { get; }

    /// <summary>
    ///     Closes the job handle, which is what terminates anything still assigned to it. Disposed by the
    ///     container at host shutdown — after the hosted services have stopped, so an orderly drain still goes
    ///     first and this stays the backstop rather than the mechanism.
    /// </summary>
    public void Dispose()
    {
        _job?.Dispose();
    }

    /// <summary>
    ///     The command to spawn for <paramref name="fileName" />: on Linux the <c>setpriv</c> wrapper, and
    ///     elsewhere the caller's own command unchanged.
    /// </summary>
    internal SpawnCommand Rewrite(string fileName, IReadOnlyList<string> arguments)
    {
        if (!OperatingSystem.IsLinux() || _setprivPath is null) return SpawnCommand.AsRequested(fileName, arguments);

        // Resolved here rather than left to setpriv, so a command that does not exist fails exactly as it
        // does today — a Win32Exception from this process, not a setpriv exec error.
        string? target = ParentDeathSignal.Resolve(fileName, _environment.GetVariable(PathVariable));

        return ParentDeathSignal.Wrap(_setprivPath, target, fileName, arguments);
    }

    /// <summary>
    ///     Start <paramref name="process" /> and bind its lifetime to this one. A missing executable throws
    ///     <see cref="Win32Exception" /> from here exactly as <see cref="Process.Start()" /> does, wrapped or
    ///     not.
    /// </summary>
    /// <param name="process">The process to start, built from the command <see cref="Rewrite" /> produced.</param>
    /// <param name="wrapped">
    ///     <see cref="SpawnCommand.Wrapped" /> from that same command, rather than re-derived here: when
    ///     <see cref="ParentDeathSignal.Wrap" /> declines, only the command knows, and a re-derivation from
    ///     platform and <c>setpriv</c> would report a signal this child never got.
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
    ///     The job, or <see langword="null" /> after one warning. Creating one is a handful of instructions
    ///     against no quota and no permission, so a failure is a genuine surprise and worth saying out loud
    ///     once — but never worth failing a server over, since the whole feature is a backstop.
    /// </summary>
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

    /// <summary>
    ///     Fork on a thread that outlives every run. <c>PR_SET_PDEATHSIG</c> fires when the <em>thread</em>
    ///     that forked the child exits, not when the process does, and <see cref="Process.Start()" /> forks
    ///     on the calling thread — which under <c>async</c> is a thread-pool thread free to retire mid-run
    ///     and <c>SIGKILL</c> a perfectly healthy <c>jb</c>. One background thread, created on first use and
    ///     never exited, is what makes the signal mean what it is meant to mean.
    /// </summary>
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

        // Blocking on purpose: Process.Start is synchronous wherever it runs, and the caller above is
        // written around a spawn that has either happened or thrown by the time it returns. A faulted task
        // rethrows here with the original type and stack, so a missing executable still surfaces as the
        // Win32Exception every caller already handles.
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

    /// <summary>
    ///     Say what this child actually got, not what the platform offers: an assignment the kernel refused
    ///     leaves one unbound child in a process whose fingerprint says otherwise, and that difference is the
    ///     only thing a log can still tell a reader afterwards.
    /// </summary>
    private void Report(Process process, string guarantee)
    {
        _logger.LogDebug(
            "Started {ChildFileName} as pid {ChildProcessId}, orphan guard {OrphanGuard}",
            process.StartInfo.FileName,
            process.Id,
            guarantee);
    }
}