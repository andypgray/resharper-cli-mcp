using Shouldly;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>Asserts on text the product writes.</summary>
[ShouldlyMethods]
internal static class StringAssertions
{
    /// <summary>
    ///     Asserts that every one of <paramref name="fragments" /> occurs in <paramref name="actual" />, ordinally,
    ///     and that each first occurrence comes after the one before it.
    /// </summary>
    /// <remarks>
    ///     Presence is asserted before order, because an <c>IndexOf</c> pair alone reads a missing fragment as -1,
    ///     which sorts ahead of every fragment that is there.
    /// </remarks>
    public static void ShouldContainInOrder(this string actual, params string[] fragments)
    {
        int previous = -1;
        string? previousFragment = null;
        foreach (string fragment in fragments)
        {
            int at = actual.IndexOf(fragment, StringComparison.Ordinal);
            at.ShouldBeGreaterThanOrEqualTo(0, $"\"{fragment}\" is missing from:\n{actual}");
            at.ShouldBeGreaterThan(previous, $"\"{fragment}\" first occurs before \"{previousFragment}\" in:\n{actual}");
            previous = at;
            previousFragment = fragment;
        }
    }
}