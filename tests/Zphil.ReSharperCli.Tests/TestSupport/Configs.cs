using Zphil.ReSharperCli.Discovery;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     The <see cref="ResolvedConfig" /> for tests that enter below <c>ConfigResolver</c>: a solution path
///     and a cache home carrying meaning, every optional axis absent, and <c>jb</c> resolved by bare name.
///     One spelling, so growing the record ripples here rather than through every service test.
/// </summary>
internal static class Configs
{
    /// <summary>
    ///     <paramref name="jbVersion" /> defaults to none — the off switch for everything keyed by it, so a
    ///     test that does not care about builds reads exactly as it did before the marker recorded one.
    /// </summary>
    public static ResolvedConfig Bare(string solutionPath, string cacheHome, string? jbVersion = null)
    {
        return new ResolvedConfig(
            solutionPath, null, false, null, cacheHome, null, null, "jb", ConfigWarnings.None, jbVersion);
    }
}