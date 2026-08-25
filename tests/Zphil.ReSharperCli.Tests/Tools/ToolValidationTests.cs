using NSubstitute;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Tests.Tools;

/// <summary>
///     Input validation the tool methods still perform themselves before any work is dispatched: a file
///     list that is empty, null, or carries a blank entry must throw a <see cref="UserErrorException" />
///     without ever probing jb. Invalid <c>severity</c> is no longer validated here — it is an enum now,
///     validated at the argument-binding layer (see <c>EnumValidationConverterTests</c> and
///     <c>CoercionIntegrationTests</c>).
/// </summary>
public sealed class ToolValidationTests
{
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CleanupAsync_EmptyFiles_ThrowsUserErrorAndDoesNotProbeJb()
    {
        // Arrange
        using FakeEnvironment environment = new();
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => tools.CleanupAsync([], cancellationToken: Ct));

        // Assert
        exception.Message.ShouldBe("At least one file must be specified.");
        await _processRunner.DidNotReceive().AnyRun();
    }

    [Fact]
    public async Task CleanupAsync_NullFiles_ThrowsUserError()
    {
        // Arrange
        using FakeEnvironment environment = new();
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => tools.CleanupAsync(null!, cancellationToken: Ct));

        // Assert
        exception.Message.ShouldBe("At least one file must be specified.");
    }

    [Fact]
    public async Task CleanupAsync_BlankFileEntry_ThrowsUserErrorNamingThePositionAndDoesNotProbeJb()
    {
        // Arrange — a blank entry names no file and would throw out of path resolution as an internal
        // error. This tool rewrites what it is given, so the whole list is rejected rather than partly run.
        using FakeEnvironment environment = new();
        ResharperTools tools = ToolHarness.Build(_processRunner, environment);

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => tools.CleanupAsync(["src/A.cs", "  "], cancellationToken: Ct));

        // Assert
        exception.Message.ShouldBe("File paths must not be blank (files[1] is empty).");
        await _processRunner.DidNotReceive().AnyRun();
    }
}