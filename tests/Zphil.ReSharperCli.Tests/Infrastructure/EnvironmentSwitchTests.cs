using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Tests.Infrastructure;

public sealed class EnvironmentSwitchTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("on", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("something unrecognised", true)]
    [InlineData("off", false)]
    [InlineData("OFF", false)]
    [InlineData("  off  ", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("disabled", false)]
    public void IsOn_TreatsTheDocumentedSpellingsAsOffAndEverythingElseAsOn(string? value, bool expected)
    {
        // Assert — an unrecognised value falls back to the shipped default, as an unrecognised log level does.
        // `off` is the spelling the docs teach; the rest are what a user might reasonably try.
        EnvironmentSwitch.IsOn(value).ShouldBe(expected);
    }
}