using System.Diagnostics;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     The primitive the Windows half of the orphan guard rests on, exercised against real processes: a
///     child assigned to the job is terminated by the kernel when the job's last handle closes.
/// </summary>
/// <remarks>
///     Worth testing directly rather than only through <see cref="ProcessRunner" />, because the thing being
///     claimed is a property of the OS rather than of this code — and because the failure it guards against
///     is invisible in a working session. A job that was created but never armed, or an assignment that
///     silently did not take, behaves identically to a working one right up until a server is killed.
/// </remarks>
public sealed class WindowsJobObjectTests
{
    private const string NotWindows = "A job object is a Windows primitive.";

    /// <summary>Read by <c>SkipUnless</c> on every method below.</summary>
    public static bool OnWindows => OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Skip = NotWindows, SkipUnless = nameof(OnWindows))]
    public async Task Dispose_TerminatesAChildAssignedToTheJob()
    {
        // Arrange — a child that would otherwise outlive this test by minutes, which is what makes its exit
        // attributable to the job rather than to it having finished anyway.
        var job = WindowsJobObject.Create();
        using Process child = StartLongLivedProcess();

        try
        {
            job.TryAssign(child).ShouldBeTrue();
            child.HasExited.ShouldBeFalse();

            // Act — the whole mechanism. On an ungraceful kill the OS does this on the process's behalf.
            job.Dispose();

            // Assert
            await child.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
            child.HasExited.ShouldBeTrue();
        }
        finally
        {
            KillIfRunning(child);
        }
    }

    [Fact(Skip = NotWindows, SkipUnless = nameof(OnWindows))]
    public async Task TryAssign_AChildThatHasAlreadyExited_ReportsFailureRatherThanThrowing()
    {
        // Arrange — the ordinary race: a spawn short enough to be over before the assignment lands. Windows
        // refuses it with ERROR_ACCESS_DENIED, which is an outcome rather than a fault.
        using var job = WindowsJobObject.Create();
        using Process child = Process.Start(new ProcessStartInfo("cmd", ["/c", "exit 0"]) { UseShellExecute = false, CreateNoWindow = true })!;
        await child.WaitForExitAsync(Ct);

        // Act / Assert
        job.TryAssign(child).ShouldBeFalse();
    }

    private static Process StartLongLivedProcess()
    {
        ProcessStartInfo startInfo = new("ping", ["-n", "300", "127.0.0.1"])
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };

        return Process.Start(startInfo)!;
    }

    private static void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch
        {
            // An assertion has already failed by the time this matters; a cleanup throw would only hide it.
        }
    }
}