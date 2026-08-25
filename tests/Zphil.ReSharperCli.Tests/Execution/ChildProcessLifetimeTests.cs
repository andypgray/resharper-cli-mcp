using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     The platform switch: which guarantee this process got, and what it does on a platform that offers
///     none.
/// </summary>
public sealed class ChildProcessLifetimeTests
{
    /// <summary>Read by <c>SkipUnless</c>: macOS today, and any future platform with nothing to reach for.</summary>
    public static bool WithoutAPrimitive => !OperatingSystem.IsWindows() && !OperatingSystem.IsLinux();

    /// <summary>Read by <c>SkipUnless</c> on the case that pins which primitive Windows resolves to.</summary>
    public static bool OnWindows => OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Guarantee_IsOneOfTheThreeTheServerKnowsHowToReport()
    {
        // Assert — the value rides on the startup fingerprint, so an unrecognised one would reach a reader as
        // a field they have no way to interpret.
        using ChildProcessLifetime lifetime = Lifetime();

        lifetime.Guarantee.ShouldBeOneOf(
            ChildProcessLifetime.KillOnJobClose,
            ChildProcessLifetime.ParentDeathSignalled,
            ChildProcessLifetime.NoGuarantee);
    }

    [Fact(Skip = "The job object is a Windows primitive.", SkipUnless = nameof(OnWindows))]
    public void Guarantee_OnWindows_IsTheJobObject()
    {
        // Assert — a job creation that quietly failed would leave a server reporting "none" and behaving as
        // it did before, which is safe but is not the fix.
        using ChildProcessLifetime lifetime = Lifetime();

        lifetime.Guarantee.ShouldBe(ChildProcessLifetime.KillOnJobClose);
    }

    [Fact(Skip = "The job object is a Windows primitive.", SkipUnless = nameof(OnWindows))]
    public void Rewrite_OnWindows_LeavesTheCommandAlone()
    {
        // Assert — the job binds a process that has already started, so nothing stands between the caller's
        // command and the one that runs. Only the Linux path rewrites.
        using ChildProcessLifetime lifetime = Lifetime();

        SpawnCommand command = lifetime.Rewrite("jb", ["inspectcode", "App.sln"]);

        command.FileName.ShouldBe("jb");
        command.Arguments.ShouldBe(["inspectcode", "App.sln"]);
        command.Wrapped.ShouldBeFalse();
    }

    [Fact(Skip = "Windows and Linux both have a primitive to apply.", SkipUnless = nameof(WithoutAPrimitive))]
    public async Task Dispose_WhereThePlatformOffersNoPrimitive_LeavesARunningChildExactlyAsItWas()
    {
        // Arrange — the deliberate limit rather than an oversight: a platform with no equivalent keeps
        // today's behaviour rather than gaining a heuristic that is wrong in ways nobody can predict.
        ChildProcessLifetime lifetime = Lifetime();
        lifetime.Guarantee.ShouldBe(ChildProcessLifetime.NoGuarantee);

        SpawnCommand command = lifetime.Rewrite("sleep", ["30"]);
        command.FileName.ShouldBe("sleep");
        command.Arguments.ShouldBe(["30"]);

        using Process child = new();
        child.StartInfo = new ProcessStartInfo("sleep", ["30"]) { UseShellExecute = false, CreateNoWindow = true };
        lifetime.Start(child, command.Wrapped);

        try
        {
            // Act
            lifetime.Dispose();

            // Assert — still running a second later, which is the behaviour this platform has always had.
            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            child.HasExited.ShouldBeFalse();
        }
        finally
        {
            if (!child.HasExited) child.Kill(true);
        }
    }

    private static ChildProcessLifetime Lifetime()
    {
        // The real environment: PATH is what the Linux half resolves setpriv and the target against, and a
        // fake with no PATH would report "none" on the one platform whose wrap is worth exercising.
        return new ChildProcessLifetime(new SystemEnvironment(), NullLogger<ChildProcessLifetime>.Instance);
    }
}