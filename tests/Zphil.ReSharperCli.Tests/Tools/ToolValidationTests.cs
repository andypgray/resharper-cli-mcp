using NSubstitute;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Tests.Tools;

/// <summary>
///     Input validation the tool methods still perform themselves before any work is dispatched: an
///     empty or null file list must throw a <see cref="UserErrorException" /> without ever probing jb.
///     Invalid <c>severity</c> is no longer validated here — it is an enum now, validated at the
///     argument-binding layer (see <c>EnumValidationConverterTests</c> and <c>CoercionIntegrationTests</c>).
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
        await _processRunner.DidNotReceive().RunAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
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
}