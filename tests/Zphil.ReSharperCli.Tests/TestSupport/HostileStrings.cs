using FsCheck;
using FsCheck.Fluent;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>What a totality property draws from, and how a failure quotes what it drew.</summary>
internal static class HostileStrings
{
    /// <summary>Any string at all, or one of <paramref name="corpus" />.</summary>
    /// <remarks>
    ///     The corpus is the strings known to sit on an edge, unioned in so every edge is hit on every seed
    ///     rather than waited for.
    /// </remarks>
    internal static Gen<string> AnyOr(params string[] corpus)
    {
        Gen<string> arbitrary = ArbMap.Default.GeneratorFor<string>().Where(value => value is not null);
        return Gen.OneOf(arbitrary, Gen.Elements(corpus));
    }

    /// <summary>Enough of <paramref name="value" /> to recognise it in a failure.</summary>
    /// <remarks>A counterexample forty thousand characters long is no help read whole.</remarks>
    internal static string Excerpt(string value)
    {
        return value.Length <= 60 ? value : value[..60] + "...";
    }
}