using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     The Linux wrapper's argument vector, pinned on every platform. Locating <c>setpriv</c> and probing it
///     need a Linux machine; composing the command it will be spawned with does not, and that composition is
///     where the two things worth getting wrong live — the shape <c>setpriv</c> expects, and whether the wrap
///     happens at all.
/// </summary>
public sealed class ParentDeathSignalTests
{
    [Fact]
    public void Wrap_ASetprivAndAResolvedTarget_ProducesTheVectorSetprivExpects()
    {
        // Act
        SpawnCommand command = ParentDeathSignal.Wrap("/usr/bin/setpriv", "/home/u/.dotnet/tools/jb", "jb", ["inspectcode", "App.sln"]);

        // Assert — the `--` separates setpriv's own options from the target's, which matters the moment a
        // target argument starts with a dash, and every jb argument does.
        command.FileName.ShouldBe("/usr/bin/setpriv");
        command.Arguments.ShouldBe(["--pdeathsig", "SIGKILL", "--", "/home/u/.dotnet/tools/jb", "inspectcode", "App.sln"]);
    }

    [Fact]
    public void Wrap_AWrappedCommand_SaysItWrapped()
    {
        // Assert — the flag is the decision Start consumes: a spawn reported as parent-death-signalled must
        // be one this method actually wrapped.
        ParentDeathSignal.Wrap("/usr/bin/setpriv", "/usr/bin/jb", "jb", []).Wrapped.ShouldBeTrue();
    }

    [Fact]
    public void Wrap_ATargetThatCouldNotBeResolved_DeclinesToWrapIt()
    {
        // Arrange — jb is not installed, so nothing on PATH resolved. Wrapped, the spawn would succeed and
        // exit non-zero with a setpriv exec error, where JbLocator's probe expects the Win32Exception a
        // missing executable has always thrown.
        SpawnCommand command = ParentDeathSignal.Wrap("/usr/bin/setpriv", null, "jb", ["inspectcode", "--version"]);

        // Assert
        command.FileName.ShouldBe("jb");
        command.Arguments.ShouldBe(["inspectcode", "--version"]);
        command.Wrapped.ShouldBeFalse();
    }

    [Fact]
    public void Wrap_NoArguments_StillWraps()
    {
        // Assert — the vector is well-formed with an empty tail, so the wrap does not quietly depend on the
        // caller having passed something.
        ParentDeathSignal.Wrap("/usr/bin/setpriv", "/usr/bin/jb", "jb", [])
            .Arguments.ShouldBe(["--pdeathsig", "SIGKILL", "--", "/usr/bin/jb"]);
    }
}