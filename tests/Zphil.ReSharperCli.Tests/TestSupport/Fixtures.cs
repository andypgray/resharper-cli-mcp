namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Helpers for reading test fixtures copied next to the test assembly (see the csproj
///     <c>Content Include="Fixtures/**"</c> item).
/// </summary>
internal static class Fixtures
{
    /// <summary>Absolute path to a fixture file or directory under <c>Fixtures/</c>, named segment by segment.</summary>
    public static string PathTo(params string[] segments)
    {
        string[] path = [AppContext.BaseDirectory, "Fixtures", .. segments];
        return Path.Combine(path);
    }

    /// <summary>Reads the text of a SARIF fixture under <c>Fixtures/Sarif/</c>.</summary>
    public static string ReadSarif(string fileName)
    {
        return File.ReadAllText(PathTo("Sarif", fileName));
    }
}