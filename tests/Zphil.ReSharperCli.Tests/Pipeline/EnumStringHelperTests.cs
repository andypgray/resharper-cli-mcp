using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Pipeline;

namespace Zphil.ReSharperCli.Tests.Pipeline;

/// <summary>
///     Pins <see cref="EnumStringHelper.LooksNumeric" /> on the trap cases a naive leading-digit check would
///     miss.
/// </summary>
public sealed class EnumStringHelperTests
{
    [Theory]
    [InlineData("5")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("+5")]
    [InlineData(" 5 ")]
    [InlineData("5 ")]
    [InlineData(" 5")]
    [InlineData("99999999999999999999999")] // wider than Int64 — caught by BigInteger, not long
    public void LooksNumeric_IntegerStrings_ReturnsTrue(string value)
    {
        EnumStringHelper.LooksNumeric(value).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Warning")]
    [InlineData("Warning5")]
    [InlineData("5Warning")]
    [InlineData("5x")]
    [InlineData("0x10")]
    [InlineData("")]
    [InlineData("   ")]
    public void LooksNumeric_NonIntegerStrings_ReturnsFalse(string value)
    {
        EnumStringHelper.LooksNumeric(value).ShouldBeFalse();
    }
}