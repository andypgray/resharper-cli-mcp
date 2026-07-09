using System.Numerics;

namespace Zphil.ReSharperCli.Pipeline;

/// <summary>
///     Helpers for enum tool-input coercion shared by <see cref="EnumArrayCoercerFactory" /> and
///     <see cref="EnumValidationConverterFactory" />.
/// </summary>
internal static class EnumStringHelper
{
    /// <summary>
    ///     Returns <c>true</c> when <paramref name="value" /> is an integer in disguise — a string
    ///     <see cref="Enum.TryParse{T}(string,bool,out T)" /> would bind to a numeric ordinal rather
    ///     than a name (e.g. <c>"5"</c> → <c>Method</c>), violating the documented "integers are not
    ///     admitted as enum values" contract.
    /// </summary>
    /// <remarks>
    ///     Trims surrounding whitespace first, then tests
    ///     <see cref="long.TryParse(ReadOnlySpan{char}, out long)" /> and
    ///     <see cref="BigInteger.TryParse(ReadOnlySpan{char}, out BigInteger)" />. A leading-digit
    ///     check is insufficient: <c>"5"</c>, <c>"+5"</c>, <c>" 5 "</c>, <c>"5 "</c>, and ordinals
    ///     wider than <see cref="long" /> must all be rejected.
    /// </remarks>
    internal static bool LooksNumeric(string value)
    {
        var trimmed = value.AsSpan().Trim();
        return long.TryParse(trimmed, out _) || BigInteger.TryParse(trimmed, out _);
    }
}