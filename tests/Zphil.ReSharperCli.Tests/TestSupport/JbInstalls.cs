using Zphil.ReSharperCli.Discovery;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>Plants the files a <c>jb</c> install leaves where <see cref="JbLocator" /> looks.</summary>
/// <remarks>
///     Nothing planted here runs: the files exist only to be found and stat'ed, so a test of what a running
///     server does when its <c>jb</c> is replaced pairs them with a faked process runner.
/// </remarks>
internal static class JbInstalls
{
    /// <summary>
    ///     Plants a <c>jb</c> in <paramref name="directory" /> the way a PATH search finds it, and returns the
    ///     file's path.
    /// </summary>
    /// <remarks>
    ///     <c>jb.exe</c> on Windows, and <c>jb</c> with its execute bit elsewhere, without which the search
    ///     passes it over.
    /// </remarks>
    public static string PlantJbIn(string directory)
    {
        string file = Path.Combine(directory, OperatingSystem.IsWindows() ? "jb.exe" : "jb");
        PlantExecutable(file);
        return file;
    }

    /// <summary>
    ///     Plants the <c>dotnet tool</c> shim under <paramref name="home" />, at the path the locator's own
    ///     global-tools candidate names, and returns the file's path.
    /// </summary>
    /// <remarks>Taking the path from the locator makes the file planted the one the product looks for.</remarks>
    public static string PlantGlobalToolShim(string home)
    {
        string file = JbLocator.Candidates(home).Last();
        PlantExecutable(file);
        return file;
    }

    /// <summary>
    ///     What <c>dotnet tool update</c> does to the shim, as far as a stat can tell: the write time moves on
    ///     and the size stays, since the shim is an apphost whose size does not depend on the build it starts.
    /// </summary>
    public static void UpdateInPlace(string file)
    {
        DateTime written = File.GetLastWriteTimeUtc(file);
        File.SetLastWriteTimeUtc(file, written + TimeSpan.FromMinutes(1));
    }

    private static void PlantExecutable(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "jb");

        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, File.GetUnixFileMode(file) | UnixFileMode.UserExecute);
    }
}