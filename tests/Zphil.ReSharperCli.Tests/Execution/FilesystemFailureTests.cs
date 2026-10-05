using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     Pins the set <see cref="FilesystemFailure" /> covers, subclasses included, and what it must never cover
///     as "the disk said no" — above all a cancellation.
/// </summary>
public sealed class FilesystemFailureTests
{
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(DirectoryNotFoundException))] // a subclass rides along with its base
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(ArgumentException))] // an invalid path: an embedded NUL, a bad character
    [InlineData(typeof(ArgumentNullException))]
    public void Covers_AnOrdinaryFilesystemMishap_IsTrue(Type exceptionType)
    {
        // Arrange
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        // Act & Assert
        FilesystemFailure.Covers(exception).ShouldBeTrue();
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NullReferenceException))]
    public void Covers_AnythingElse_IsFalse(Type exceptionType)
    {
        // Arrange
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        // Act & Assert
        FilesystemFailure.Covers(exception).ShouldBeFalse();
    }
}