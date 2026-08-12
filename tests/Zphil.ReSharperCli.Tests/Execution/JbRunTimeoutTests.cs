using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     How <c>RESHARPER_MCP_TIMEOUT_SECS</c> reads. A pure <c>(string?) → TimeSpan</c>, which is what keeps
///     the variable out of the parallel suite's way: nothing here touches real process environment. The rule
///     it encodes is the one the other variables follow — a value nobody can make sense of costs the shipped
///     default, never a failed call — and the clamps exist so a plausible typo cannot turn the cap into
///     something worse than having no lever at all.
/// </summary>
public sealed class JbRunTimeoutTests
{
    [Fact]
    public void Default_IsSixHundredSeconds()
    {
        // The number itself is the fix, not just the lever: a cold whole-solution analysis needs more than
        // the five minutes this server used to allow, and no MCP client imposes a shorter limit of its own.
        JbRunTimeout.Default.ShouldBe(TimeSpan.FromSeconds(600));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ten")]
    [InlineData("600s")]
    [InlineData("10 minutes")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("0")]
    [InlineData("-5")]
    public void Resolve_UnusableValue_FallsBackToTheDefault(string? envValue)
    {
        // A misconfigured variable must never be the reason a call fails, so every unreadable spelling —
        // including the non-finite ones that would throw out of TimeSpan.FromSeconds — lands on the default.
        JbRunTimeout.Resolve(envValue).ShouldBe(JbRunTimeout.Default);
    }

    [Theory]
    [InlineData("1200", 1200)]
    [InlineData("  1200  ", 1200)]
    [InlineData("90", 90)]
    [InlineData("1e3", 1000)]
    public void Resolve_UsableValue_IsThatManySeconds(string envValue, double expectedSeconds)
    {
        // Seconds, not minutes: the point of the finer unit is that a cap can sit just above a measured
        // cold run rather than at the next whole minute up.
        JbRunTimeout.Resolve(envValue).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData("30")]
    [InlineData("0.5")]
    public void Resolve_BelowTheFloor_IsRaisedToOneMinute(string envValue)
    {
        // A cap under a minute would kill warm runs that were never in trouble, so it reads as a mistake
        // rather than as an instruction to make the server useless.
        JbRunTimeout.Resolve(envValue).ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Theory]
    [InlineData("100000")]
    [InlineData("1e300")]
    public void Resolve_AboveTheCeiling_IsCappedAtADay(string envValue)
    {
        // Bounding a hung jb is the whole point of having a cap; a value large enough to overflow
        // TimeSpan.FromSeconds must clamp rather than throw on the way past.
        JbRunTimeout.Resolve(envValue).ShouldBe(TimeSpan.FromHours(24));
    }

    [Fact]
    public void Resolve_ThousandsSeparator_FallsBackRatherThanGuessingWhichNumberWasMeant()
    {
        // Parsed invariantly and without thousands grouping, which matters most at this scale: "1,200" is a
        // plausible way to write 1200 seconds, and a parser that accepted it under one culture and read it
        // as 1.2 under another would hand two machines different caps from the same config.
        JbRunTimeout.Resolve("1,200").ShouldBe(JbRunTimeout.Default);
    }
}