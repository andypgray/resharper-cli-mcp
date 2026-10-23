using System.Text.RegularExpressions;
using Shouldly;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>Assertions over the product csproj, which the version, icon and description sites are held to.</summary>
internal static class ProductCsprojAssertions
{
    /// <summary>
    ///     Asserts that the product csproj declares <paramref name="element" />, failing with
    ///     <paramref name="because" /> when it does not, and returns the element's text.
    /// </summary>
    public static string ShouldDeclareCsprojElement(string element, string because)
    {
        string csproj = RepoRoot.ReadText(RepoRoot.ProductCsproj);
        string tag = Regex.Escape(element);
        Match match = Regex.Match(csproj, $"<{tag}>(?<text>.*?)</{tag}>", RegexOptions.Singleline);
        match.Success.ShouldBeTrue(because);

        return match.Groups["text"].Value;
    }
}