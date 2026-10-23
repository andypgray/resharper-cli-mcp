using Shouldly;
using Xunit;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Pins that the case-sensitive overloads in <see cref="CaseSensitiveStringAssertions" /> are the ones a test
///     file binds to.
/// </summary>
/// <remarks>
///     Declared in a nested namespace because the overloads bind only through extension lookup reaching the
///     suite's root namespace before the compilation unit's <c>using Shouldly</c>, and only a file below the root
///     can show that. An overload that stops comparing case, or that a call with a message no longer reaches,
///     reds this.
/// </remarks>
public sealed class CaseSensitiveStringAssertionsTests
{
    private const string Subject = "Zphil.ReSharperCli";

    private const string Said = "the caller's message";

    [Fact]
    public void ShouldContain_WrongCase_Reds()
    {
        Should.Throw<ShouldAssertException>(() => Subject.ShouldContain("resharpercli"));
        Subject.ShouldContain("ReSharperCli");
    }

    [Fact]
    public void ShouldStartWith_WrongCase_Reds()
    {
        Should.Throw<ShouldAssertException>(() => Subject.ShouldStartWith("zphil."));
        Subject.ShouldStartWith("Zphil.");
    }

    [Fact]
    public void ShouldEndWith_WrongCase_Reds()
    {
        Should.Throw<ShouldAssertException>(() => Subject.ShouldEndWith("cli"));
        Subject.ShouldEndWith("Cli");
    }

    [Fact]
    public void WithMessage_WrongCase_RedsCarryingTheMessage()
    {
        // A message binds to the twin that takes one, which has to compare case as the bare overload does.
        Should.Throw<ShouldAssertException>(() => Subject.ShouldContain("resharpercli", Said))
            .Message.ShouldContain(Said);
        Should.Throw<ShouldAssertException>(() => Subject.ShouldStartWith("zphil.", Said))
            .Message.ShouldContain(Said);
        Should.Throw<ShouldAssertException>(() => Subject.ShouldEndWith("cli", Said))
            .Message.ShouldContain(Said);
    }

    [Fact]
    public void ExplicitInsensitive_FallsThroughToShouldly()
    {
        // The opt-out: no overload here takes a Case, so naming one binds to Shouldly's own.
        Subject.ShouldContain("resharpercli", Case.Insensitive);
        Subject.ShouldStartWith("zphil.", Case.Insensitive);
        Subject.ShouldEndWith("cli", Case.Insensitive);
    }
}