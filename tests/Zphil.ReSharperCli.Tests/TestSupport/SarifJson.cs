using System.Text;
using Zphil.ReSharperCli.Sarif;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>Parses SARIF written as a string through the stream reader the server uses.</summary>
internal static class SarifJson
{
    /// <summary>Parses <paramref name="json" /> with <see cref="SarifParser.ParseAsync" />.</summary>
    /// <remarks>
    ///     Synchronous, so a property body can call it: a read from a <see cref="MemoryStream" /> completes
    ///     without yielding, so blocking on the task waits on nothing.
    /// </remarks>
    internal static List<InspectIssue> Parse(string json)
    {
        using MemoryStream sarif = new(Encoding.UTF8.GetBytes(json));

        return SarifParser.ParseAsync(sarif, CancellationToken.None).GetAwaiter().GetResult();
    }
}