using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Builds the cache-home layouts the reset, warm-marker, and transplant tests all read: <c>jb</c>'s
///     <c>_{solution file name}.{hash}.{generation}</c> directories, with enough inside them to be copied
///     and compared.
/// </summary>
/// <remarks>
///     Shared rather than repeated per test class because the shape is a fact about <c>jb</c> rather than
///     about any one test: a generation is a directory tree with files nested inside it, not an empty
///     directory, and a copy that flattened it would pass against a flat fake.
/// </remarks>
internal static class CacheHomes
{
    /// <summary>
    ///     Plant a generation directory called <paramref name="directoryName" />, for tests that pick the
    ///     name themselves to exercise the parser. Returns its full path.
    /// </summary>
    public static string PlantGeneration(string cacheHome, string directoryName)
    {
        string generation = Path.Combine(cacheHome, directoryName);
        Directory.CreateDirectory(Path.Combine(generation, "Db"));
        File.WriteAllText(Path.Combine(generation, "Db", "CURRENT"), "cache");
        return generation;
    }

    /// <summary>
    ///     Plant the generation <c>jb</c> would create for <paramref name="solutionPath" /> — the real
    ///     computed hash, not a made-up one — so a test exercises the same name production code the server
    ///     uses. Returns its full path.
    /// </summary>
    public static string PlantGenerationFor(string cacheHome, string solutionPath)
    {
        return PlantGeneration(cacheHome, JbSolutionCacheHash.FirstGenerationDirectoryName(solutionPath));
    }

    /// <summary>
    ///     Plant a generation for <paramref name="solutionPath" /> and stamp its warm marker, which is what
    ///     a solution whose last <c>jb</c> run succeeded looks like from the outside — and therefore what a
    ///     transplant looks for in a donor. Returns the generation's full path.
    /// </summary>
    public static string PlantWarmDonor(string cacheHome, string solutionPath)
    {
        string generation = PlantGenerationFor(cacheHome, solutionPath);
        JbWarmMarker.Stamp(solutionPath, cacheHome);
        return generation;
    }
}