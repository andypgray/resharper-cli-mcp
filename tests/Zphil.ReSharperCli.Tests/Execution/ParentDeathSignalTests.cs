using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>Pins the Linux wrapper's argument vector on every platform.</summary>
/// <remarks>
///     Locating <c>setpriv</c> and probing it need a Linux machine; composing the command it will be spawned
///     with does not, and the composition is where the wrapper can go wrong: the shape <c>setpriv</c> expects,
///     and whether the wrap happens at all.
/// </remarks>
public sealed class ParentDeathSignalTests
{
    [Fact]
    public void Wrap_ASetprivAndAResolvedTarget_ProducesTheVectorSetprivExpects()
    {
        // Act
        SpawnCommand command = ParentDeathSignal.Wrap("/usr/bin/setpriv", "/home/u/.dotnet/tools/jb", "jb", ["inspectcode", "App.sln"]);

        // Assert — the `--` separates setpriv's own options from the target's, which matters the moment a
        // target argument starts with a dash, and every jb option does.
        command.FileName.ShouldBe("/usr/bin/setpriv");
        command.Arguments.ShouldBe(["--pdeathsig", "SIGKILL", "--", "/home/u/.dotnet/tools/jb", "inspectcode", "App.sln"]);

        // And the flag says so: a spawn reported as parent-death-signalled must be one this method wrapped.
        command.Wrapped.ShouldBeTrue();
    }

    [Fact]
    public void Wrap_ATargetThatCouldNotBeResolved_DeclinesToWrapIt()
    {
        // Arrange — jb is not installed, so nothing on PATH resolved. Wrapped, the spawn would succeed and
        // exit non-zero with a setpriv exec error, instead of throwing the Win32Exception a missing executable
        // throws.
        SpawnCommand command = ParentDeathSignal.Wrap("/usr/bin/setpriv", null, "jb", ["inspectcode", "--version"]);

        // Assert
        command.FileName.ShouldBe("jb");
        command.Arguments.ShouldBe(["inspectcode", "--version"]);
        command.Wrapped.ShouldBeFalse();
    }

    [Fact]
    public void Wrap_NoArguments_StillWraps()
    {
        // Assert — the vector is well-formed with an empty tail, so the wrap does not depend on the caller
        // having passed something.
        ParentDeathSignal.Wrap("/usr/bin/setpriv", "/usr/bin/jb", "jb", [])
            .Arguments.ShouldBe(["--pdeathsig", "SIGKILL", "--", "/usr/bin/jb"]);
    }
}