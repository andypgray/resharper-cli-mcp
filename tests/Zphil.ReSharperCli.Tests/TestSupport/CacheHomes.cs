using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Builds cache-home layouts: <c>jb</c>'s <c>_{solution file name}.{hash}.{generation}</c> directories,
///     with enough inside them to be copied and compared.
/// </summary>
/// <remarks>
///     Shared rather than repeated per test class because the shape is a fact about <c>jb</c> rather than
///     about any one test: a generation is a directory tree with files nested inside it, not an empty
///     directory, and a copy that flattened it would pass against a flat fake.
/// </remarks>
internal static class CacheHomes
{
    /// <summary>
    ///     Plants a generation directory called <paramref name="directoryName" /> and returns its full path.
    /// </summary>
    /// <remarks>For a test that picks the name itself to exercise the parser.</remarks>
    public static string PlantGeneration(string cacheHome, string directoryName)
    {
        string generation = Path.Combine(cacheHome, directoryName);
        Directory.CreateDirectory(Path.Combine(generation, "Db"));
        File.WriteAllText(Path.Combine(generation, "Db", "CURRENT"), "cache");
        return generation;
    }

    /// <summary>
    ///     Plants the generation <c>jb</c> would create for <paramref name="solutionPath" /> and returns its
    ///     full path.
    /// </summary>
    /// <remarks>
    ///     The name carries the real computed hash, not a made-up one, so a test exercises the same name
    ///     production code the server uses.
    /// </remarks>
    public static string PlantGenerationFor(string cacheHome, string solutionPath)
    {
        return PlantGeneration(cacheHome, JbSolutionCacheHash.FirstGenerationDirectoryName(solutionPath));
    }

    /// <summary>
    ///     Where the generation for <paramref name="solutionPath" /> sits under
    ///     <paramref name="cacheHome" />, planted or not.
    /// </summary>
    /// <remarks>For a test asserting on a directory something else is expected to create.</remarks>
    public static string GenerationPathFor(string cacheHome, string solutionPath)
    {
        return JbCacheGenerations.PathUnder(cacheHome, JbSolutionCacheHash.FirstGenerationDirectoryName(solutionPath));
    }

    /// <summary>
    ///     Holds the run-lock file for <paramref name="solutionPath" /> the way another server process's live
    ///     <c>jb</c> holds it — exclusively, until the returned stream is disposed.
    /// </summary>
    /// <remarks>
    ///     This is exactly what another holder's handle looks like to the OS, so it is how a test stands in for
    ///     a concurrent run.
    /// </remarks>
    public static FileStream HoldLockFile(string cacheHome, string solutionPath)
    {
        string lockFilePath = JbRunLock.LockFilePathFor(cacheHome, JbSidecar.ComputeKey(solutionPath, cacheHome));
        return new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>
    ///     Makes the generation at <paramref name="generationPath" /> refuse to delete, the way a <c>jb</c>
    ///     this server does not know about makes it refuse.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Dispose to let go again — a test that did not would leave its own fixture unable to clean up.
    ///     </para>
    ///     <para>
    ///         One situation, two opposite levers, and neither stands in for the other: Windows will not unlink
    ///         a file something holds open, while POSIX unlinks an open file quite happily and refuses only when
    ///         the containing directory is unwritable. Holding a handle — the obvious spelling — therefore blocks
    ///         nothing outside Windows, and a test relying on it passes there while asserting the opposite of the
    ///         truth everywhere else.
    ///     </para>
    /// </remarks>
    public static IDisposable BlockDeletionOf(string generationPath)
    {
        string directory = Path.Combine(generationPath, "Db");
        if (OperatingSystem.IsWindows()) return new FileStream(Path.Combine(directory, "CURRENT"), FileMode.Open, FileAccess.Read, FileShare.None);

        return new UnwritableDirectory(directory);
    }

    /// <summary>
    ///     A cache home nothing can bring into existence: a <em>file</em> sits where the directory should
    ///     be, so every attempt to create or write under it fails.
    /// </summary>
    /// <remarks>For pinning that a cache-home side effect degrades instead of failing its call.</remarks>
    public static string BlockedCacheHome(FakeEnvironment environment)
    {
        string blocked = Path.Combine(environment.CreateTempDirectory(), "not-a-directory");
        File.WriteAllText(blocked, string.Empty);
        return blocked;
    }

    /// <summary>
    ///     Plants the fork a concurrent <c>jb</c> creates when it cannot open
    ///     <paramref name="generationPath" /> — the same solution and hash at the next generation number — and
    ///     returns its full path.
    /// </summary>
    public static string PlantFork(string cacheHome, string generationPath)
    {
        return PlantGeneration(cacheHome, Path.GetFileName(generationPath).Replace(".00", ".01"));
    }

    /// <summary>
    ///     Plants a generation for <paramref name="solutionPath" />, stamps its warm marker, and returns the
    ///     generation's full path.
    /// </summary>
    /// <remarks>
    ///     That is what a solution whose last <c>jb</c> run succeeded looks like from the outside, and therefore
    ///     what a transplant looks for in a donor. The marker names <paramref name="jbVersion" /> as the build
    ///     that warmed it, defaulting to none — a marker that records no build, which a caller naming no build
    ///     of its own compares against as an equal.
    /// </remarks>
    public static string PlantWarmDonor(string cacheHome, string solutionPath, string? jbVersion = null)
    {
        string generation = PlantGenerationFor(cacheHome, solutionPath);
        JbWarmMarker.Stamp(solutionPath, cacheHome, NullLogger.Instance, jbVersion);
        return generation;
    }

    /// <summary>
    ///     <see cref="PlantWarmDonor" /> with a marker that records no solution path: the one-line shape, the
    ///     generation name alone, or with <paramref name="jbVersion" /> the two-line shape that adds the build.
    /// </summary>
    /// <remarks>
    ///     <see cref="JbWarmMarker.Stamp" /> never writes either shape, but earlier builds of this server left
    ///     markers in both, so every reader of a marker still has to read them.
    /// </remarks>
    public static string PlantWarmDonorFromAnEarlierBuild(string cacheHome, string solutionPath, string? jbVersion = null)
    {
        string generation = PlantGenerationFor(cacheHome, solutionPath);
        string name = Path.GetFileName(generation);
        string content = jbVersion is null ? name : $"{name}\n{jbVersion}";
        File.WriteAllText(JbWarmMarker.PathFor(solutionPath, cacheHome), content);
        return generation;
    }

    /// <summary>
    ///     Strips the write permission a directory's entries can only be unlinked through, and puts back
    ///     exactly the mode that was there rather than a guess at what it should have been.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private sealed class UnwritableDirectory : IDisposable
    {
        private const UnixFileMode Writable = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        private readonly DirectoryInfo _directory;
        private readonly UnixFileMode _original;

        public UnwritableDirectory(string directory)
        {
            _directory = new DirectoryInfo(directory);
            _original = _directory.UnixFileMode;
            _directory.UnixFileMode = _original & ~Writable;
        }

        public void Dispose()
        {
            _directory.UnixFileMode = _original;
        }
    }
}