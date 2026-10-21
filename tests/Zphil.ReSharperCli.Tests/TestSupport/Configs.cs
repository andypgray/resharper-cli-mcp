using Zphil.ReSharperCli.Discovery;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Builds the <see cref="ResolvedConfig" /> for tests that enter below <c>ConfigResolver</c>: a solution
///     path and a cache home carrying meaning, every optional axis absent unless named, and <c>jb</c> resolved
///     by bare name.
/// </summary>
/// <remarks>
///     The one place a test constructs the record, so growing it ripples here rather than through every
///     service test.
/// </remarks>
internal static class Configs
{
    /// <summary>
    ///     Builds a config with every optional axis absent. <paramref name="jbVersion" /> defaults to none,
    ///     the off switch for everything keyed by it, so a test that does not care about builds need not name
    ///     one.
    /// </summary>
    public static ResolvedConfig Bare(string solutionPath, string cacheHome, string? jbVersion = null)
    {
        return With(solutionPath, cacheHome, jbVersion: jbVersion);
    }

    /// <summary>Builds the <see cref="Bare" /> config with the settings and extension axes named.</summary>
    /// <remarks>For a test pinning what each of them puts on <c>jb</c>'s command line.</remarks>
    public static ResolvedConfig With(
        string solutionPath,
        string cacheHome,
        string? settings = null,
        bool settingsIsCustomLayer = false,
        string? extensions = null,
        string? extensionSource = null,
        string? jbVersion = null)
    {
        return new ResolvedConfig(
            solutionPath,
            settings,
            settingsIsCustomLayer,
            null,
            cacheHome,
            extensions,
            extensionSource,
            "jb",
            ConfigWarnings.None,
            jbVersion);
    }
}