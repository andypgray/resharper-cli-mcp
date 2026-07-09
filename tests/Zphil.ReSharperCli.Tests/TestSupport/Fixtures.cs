namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Helpers for reading test fixtures copied next to the test assembly (see the csproj
///     <c>Content Include="Fixtures/**"</c> item).
/// </summary>
internal static class Fixtures
{
    /// <summary>Absolute path to a SARIF fixture under <c>Fixtures/Sarif/</c>.</summary>
    public static string SarifPath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sarif", fileName);
    }

    /// <summary>Read the text of a SARIF fixture under <c>Fixtures/Sarif/</c>.</summary>
    public static string ReadSarif(string fileName)
    {
        return File.ReadAllText(SarifPath(fileName));
    }
}