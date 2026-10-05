using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     Pins the <see cref="ReleaseOnce" /> guard itself; each holder's own double-dispose test pins that the
///     holder releases through it.
/// </summary>
public sealed class ReleaseOnceTests
{
    [Fact]
    public void Dispose_CalledTwice_ReleasesOnce()
    {
        // Arrange
        var released = 0;
        ReleaseOnce handle = new(() => released++);

        // Act
        handle.Dispose();
        handle.Dispose();

        // Assert
        released.ShouldBe(1);
    }

    [Fact]
    public void Dispose_RacedFromManyThreads_ReleasesOnce()
    {
        // Arrange
        var released = 0;
        ReleaseOnce handle = new(() => Interlocked.Increment(ref released));

        // Act
        Parallel.For(0, 64, _ => handle.Dispose());

        // Assert
        released.ShouldBe(1);
    }
}