using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Zphil.ReSharperCli.Execution;

/// <summary>
///     A Windows job object carrying <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: every process assigned to it
///     is terminated by the kernel when the last handle to the job closes. Held for the life of the server
///     process, so that closing — by disposal, by a crash, or by a <c>TerminateProcess</c> no in-process
///     handler can intercept — is what kills a <c>jb</c> this server started.
/// </summary>
/// <remarks>
///     <para>
///         Job membership is inherited, so a worker <c>jb</c> spawns of its own is covered without being
///         assigned. The server process itself is deliberately <em>not</em> in the job: it would then be
///         terminated by its own <see cref="Dispose" /> during an orderly shutdown, before the logs were
///         flushed.
///     </para>
///     <para>
///         There is a window of a few microseconds between <c>CreateProcess</c> returning and the assignment
///         landing, in which a kill of this server would still strand the child. Closing it needs
///         <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c> at creation time, which means re-implementing
///         <see cref="Process.Start()" /> over <c>CreateProcess</c> — a great deal of interop to narrow a
///         window measured against a human deciding to kill a process.
///     </para>
///     <para>
///         <c>DllImport</c> rather than <c>LibraryImport</c>: the source generator emits
///         <c>unsafe</c> stubs, so four calls would cost <c>AllowUnsafeBlocks</c> across the whole assembly.
///         The platform attributes sit on the interop declarations rather than on this type, so a field of
///         this type can live in a class that compiles for every platform.
///     </para>
/// </remarks>
internal sealed class WindowsJobObject : IDisposable
{
    /// <summary><c>JobObjectExtendedLimitInformation</c>, the information class that carries the limit flags.</summary>
    private const int ExtendedLimitInformationClass = 9;

    /// <summary><c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>.</summary>
    private const uint KillOnJobCloseFlag = 0x2000;

    private readonly SafeJobHandle _handle;

    private WindowsJobObject(SafeJobHandle handle)
    {
        _handle = handle;
    }

    public void Dispose()
    {
        // The whole mechanism: this is what terminates anything still assigned.
        _handle.Dispose();
    }

    /// <summary>
    ///     Create the job and arm the kill-on-close limit, throwing <see cref="Win32Exception" /> naming the
    ///     call that failed. A throw rather than a null so the caller's warning can say <em>why</em>: the
    ///     caller degrades to today's behaviour either way, and the reason is the only part it cannot guess.
    /// </summary>
    internal static WindowsJobObject Create()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("A job object is a Windows primitive.");

        nint raw = CreateJobObject(0, 0);
        if (raw == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject failed.");

        SafeJobHandle handle = new(raw);
        try
        {
            JobObjectExtendedLimitInformation information = default;
            information.BasicLimitInformation.LimitFlags = KillOnJobCloseFlag;
            int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();

            if (!SetInformationJobObject(handle, ExtendedLimitInformationClass, ref information, size))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetInformationJobObject failed.");
        }
        catch
        {
            // An armed-less job would silently guarantee nothing, so a half-built one is not worth keeping.
            handle.Dispose();
            throw;
        }

        return new WindowsJobObject(handle);
    }

    /// <summary>
    ///     Assign <paramref name="process" /> to the job, reporting whether it took. A child that has already
    ///     exited is refused with <c>ERROR_ACCESS_DENIED</c>, which is an ordinary outcome for a spawn that
    ///     finished before the assignment could land — hence a <see langword="false" /> rather than a throw.
    /// </summary>
    internal bool TryAssign(Process process)
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            return AssignProcessToJobObject(_handle, process.Handle);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The Process object has no usable handle — never started, or already released.
            return false;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern nint CreateJobObject(nint attributes, nint name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        int informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    /// <summary>
    ///     <c>JOBOBJECT_BASIC_LIMIT_INFORMATION</c>. Only <see cref="LimitFlags" /> is set; the rest are
    ///     present because the layout is what the call reads.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    /// <summary><c>IO_COUNTERS</c>, carried by the extended limit structure and never read here.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    /// <summary><c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c>, the shape <see cref="ExtendedLimitInformationClass" /> expects.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    /// <summary>
    ///     The job handle, released through <c>CloseHandle</c>. A <see cref="SafeHandle" /> rather than a raw
    ///     <see cref="nint" /> because closing it is not a tidy-up — it is the kill.
    /// </summary>
    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeJobHandle(nint handle) : base(true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle()
        {
            return OperatingSystem.IsWindows() && CloseHandle(handle);
        }
    }
}