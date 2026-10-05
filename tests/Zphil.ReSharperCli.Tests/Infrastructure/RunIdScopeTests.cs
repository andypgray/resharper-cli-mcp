using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Tests.Infrastructure;

/// <summary>
///     <see cref="RunIdScope.Next" />: the id behind the <c>{RunId}</c> column. How the column renders inside and
///     outside a scope is pinned through the real logger arrangement in <see cref="SerilogConfigurationTests" />.
/// </summary>
public sealed class RunIdScopeTests
{
    [Fact]
    public void RunId_Increments_AndStaysFourDigitsWide()
    {
        // Act
        string first = RunIdScope.Next();
        string second = RunIdScope.Next();

        // Assert — monotonic and fixed-width, which is the whole contract: SessionId separates processes, so
        // this only has to separate work inside one, and be readable in a column. Increasing, not adjacent:
        // every tool call in the parallel run draws from the same counter, so another can land between these.
        first.Length.ShouldBe(4);
        second.Length.ShouldBe(4);
        int.Parse(second).ShouldBeGreaterThan(int.Parse(first));
    }
}