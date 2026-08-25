using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     The Linux half of "a <c>jb</c> this server started cannot outlive it":
///     <c>
///         setpriv --pdeathsig
///         SIGKILL
///     </c>
///     , which arms <c>PR_SET_PDEATHSIG</c> and then execs the real command in place, so the pid
///     the server holds is still <c>jb</c>'s and a tree kill still reaches it.
/// </summary>
/// <remarks>
///     <para>
///         Weaker than the Windows job object, and the difference is worth stating rather than implying: the
///         signal covers <c>jb</c> itself and nothing it forks afterwards, because <c>PR_SET_PDEATHSIG</c> is
///         not inherited across a fork. A worker <c>jb</c> starts can still be stranded by a hard kill of this
///         server.
///     </para>
///     <para>
///         <see cref="Wrap" /> is pure, and separate from the two probes around it, so the argument vector
///         this produces is pinned by tests on every platform rather than only on the one that runs it.
///     </para>
/// </remarks>
internal static class ParentDeathSignal
{
    /// <summary>The util-linux helper that arms the signal. Absent on a minimal image, which is an ordinary outcome.</summary>
    private const string Executable = "setpriv";

    /// <summary>Added in util-linux 2.33; older builds carry <c>setpriv</c> and reject this option outright.</summary>
    private const string Option = "--pdeathsig";

    /// <summary>
    ///     <c>SIGKILL</c> rather than <c>SIGTERM</c>: this fires only when the server has already died without
    ///     draining, so there is nobody left to observe an orderly exit and nothing to gain by asking politely.
    /// </summary>
    private const string Signal = "SIGKILL";

    /// <summary>
    ///     Long enough for a helper that execs one <c>--version</c> print, and short enough that a machine
    ///     where it hangs costs a session's startup nothing worth noticing.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     The command to spawn: wrapped when <paramref name="resolvedTarget" /> is in hand, and the caller's
    ///     own command otherwise. Whether <c>setpriv</c> is available at all is
    ///     <c>ChildProcessLifetime.Rewrite</c>'s decision, made before this is called.
    /// </summary>
    /// <remarks>
    ///     Declining on an unresolvable target is what keeps a missing <c>jb</c> reading the same as it always
    ///     has. <c>JbLocator</c> probes candidates and treats both a thrown <see cref="Win32Exception" /> and a
    ///     non-zero exit as "candidate failed"; wrapped, a candidate that is not installed would exit non-zero
    ///     with <c>setpriv: failed to execute jb: No such file or directory</c> on the way to the user-facing
    ///     "not found" message. Nothing here is worth changing that for.
    /// </remarks>
    internal static SpawnCommand Wrap(
        string setprivPath,
        string? resolvedTarget,
        string fileName,
        IReadOnlyList<string> arguments)
    {
        if (resolvedTarget is null) return SpawnCommand.AsRequested(fileName, arguments);

        // The `--` is load-bearing: without it setpriv reads the target's own leading `--` arguments as its own.
        List<string> wrapped = [Option, Signal, "--", resolvedTarget, .. arguments];

        return new SpawnCommand(setprivPath, wrapped, true);
    }

    /// <summary>
    ///     Where <c>setpriv</c> is, when it is installed and accepts <see cref="Option" />, and
    ///     <see langword="null" /> otherwise — at which point the server keeps today's behaviour.
    /// </summary>
    [SupportedOSPlatform("linux")]
    internal static string? TryLocate(string? pathVariable)
    {
        string? setpriv = Resolve(Executable, pathVariable);

        return setpriv is not null && Accepts(setpriv) ? setpriv : null;
    }

    /// <summary>
    ///     What <c>execvp</c> would find for <paramref name="fileName" />: the file itself when the name
    ///     carries a separator, otherwise the first executable of that name on <paramref name="pathVariable" />.
    ///     <see langword="null" /> means "nothing to run", which is the answer <see cref="Wrap" /> declines on.
    /// </summary>
    [SupportedOSPlatform("linux")]
    internal static string? Resolve(string fileName, string? pathVariable)
    {
        if (fileName.Contains('/')) return IsExecutableFile(fileName) ? fileName : null;

        if (string.IsNullOrEmpty(pathVariable)) return null;

        foreach (string directory in pathVariable.Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;

            string candidate = Path.Combine(directory, fileName);
            if (IsExecutableFile(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    ///     Whether this <c>setpriv</c> accepts the option, asked by running it. The probe execs
    ///     <c>setpriv</c> through itself, so it needs nothing on the machine that <c>setpriv</c> being there
    ///     has not already proved — and it exercises the exact argument shape a real spawn will use rather
    ///     than a version number standing in for it.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private static bool Accepts(string setprivPath)
    {
        ProcessStartInfo probe = new()
        {
            FileName = setprivPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        string[] arguments = [Option, Signal, "--", setprivPath, "--version"];
        foreach (string argument in arguments) probe.ArgumentList.Add(argument);

        try
        {
            using Process? process = Process.Start(probe);
            if (process is null) return false;

            // Neither stream is drained: the whole output is one version line or one usage error, both far
            // inside a pipe buffer.
            if (process.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
                return process.ExitCode == 0;

            process.Kill(true);

            return false;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("linux")]
    private static bool IsExecutableFile(string candidate)
    {
        try
        {
            if (!File.Exists(candidate)) return false;

            UnixFileMode mode = File.GetUnixFileMode(candidate);

            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            return false;
        }
    }
}