using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Pipeline;

namespace Zphil.ReSharperCli.Tests.Pipeline;

/// <summary>
///     Unit tests for <see cref="EnumStringHelper.LooksNumeric" /> — the guard that keeps numeric
///     strings from binding to enum ordinals. A leading-digit check is insufficient, so these pin
///     the trap cases a naive first fix would miss.
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