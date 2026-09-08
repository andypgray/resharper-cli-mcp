using FsCheck;
using FsCheck.Fluent;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     What a totality property draws from: arbitrary strings unioned with a curated corpus of the ones known
///     to sit on an edge, so every edge is hit on every seed rather than waited for. And the excerpt a failure
///     prints, because a counterexample forty thousand characters long is no help read whole.
/// </summary>
internal static class HostileStrings
{
    /// <summary>Any string at all, or one of <paramref name="corpus" />.</summary>
    internal static Gen<string> AnyOr(params string[] corpus)
    {
        Gen<string> arbitrary = ArbMap.Default.GeneratorFor<string>().Where(value => value is not null);
        return Gen.OneOf(arbitrary, Gen.Elements(corpus));
    }

    /// <summary>Enough of <paramref name="value" /> to recognise it in a failure.</summary>
    internal static string Excerpt(string value)
    {
        return value.Length <= 60 ? value : value[..60] + "...";
    }
}