using Shouldly;
using Zphil.ReSharperCli.Discovery;

namespace Zphil.ReSharperCli.Tests.Discovery;

/// <summary>Assertions over what a read of the project model produced.</summary>
internal static class ModelReadingAssertions
{
    /// <summary>
    ///     Asserts that <paramref name="reading" /> produced a value, failing with the reason it declined, and returns
    ///     the value.
    /// </summary>
    public static T ShouldHaveValue<T>(this ModelReading<T> reading) where T : class
    {
        return reading.Value.ShouldNotBeNull(reading.Declined);
    }

    /// <summary>Asserts that <paramref name="reading" /> declined, and returns its reason.</summary>
    public static string ShouldHaveDeclined<T>(this ModelReading<T> reading) where T : class
    {
        reading.Value.ShouldBeNull();
        return reading.Declined.ShouldNotBeNull();
    }
}