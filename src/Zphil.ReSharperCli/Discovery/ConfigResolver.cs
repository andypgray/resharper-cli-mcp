using Serilog;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Discovery;

/// <summary>
///     What went wrong while resolving configuration, in a form a tool can report rather than only log.
///     Neither of these fails a call — both degrade it silently, which is exactly why they have to be said
///     out loud, and their blast radii differ: <see cref="MissingSettingsPath" /> means the settings file
///     the user named was never applied, taking inspection severities and cleanup profiles with it, while
///     <see cref="SettingsRead" /> means <c>jb</c> got the file and parsed it fine and only this server's
///     own profile lookup failed, so cleanup silently fell back to a broader profile.
/// </summary>
internal sealed record ConfigWarnings(string? MissingSettingsPath, SettingsReadFailure? SettingsRead);

/// <summary>Everything needed to shell out to <c>jb</c>: the solution, optional settings, cache home, and extensions.</summary>
internal sealed record ResolvedConfig(
    string SolutionPath,
    string? SettingsPath,
    string? CleanupProfile,
    string CacheHome,
    string? Extensions,
    string? ExtensionSource,
    string JbExecutablePath,
    ConfigWarnings? Warnings = null);

/// <summary>
///     Resolves the <see cref="ResolvedConfig" /> for a request: verifies <c>jb</c> is installed, then
///     locates the solution, settings, declared cleanup profile, cache home, and extension defaults from
///     overrides, environment variables, and the current directory.
/// </summary>
/// <remarks>
///     Deliberately uncached. Every field but the <c>jb</c> path is re-read from disk on each call, so a
///     settings file added or edited mid-session — notably one newly declaring a cleanup profile, which is
///     exactly what the configuration guide tells an agent to do — takes effect on the next call instead of
///     after a client restart. The one genuinely expensive step, the <c>jb inspectcode --version</c> probe,
///     is cached for the process inside <see cref="JbLocator" />; what remains here is one directory
///     enumeration, a few existence checks, and a small XML read — noise beside the jb run that follows.
/// </remarks>
internal sealed class ConfigResolver(JbLocator jbLocator, IEnvironment environment)
{
    public async Task<ResolvedConfig> ResolveAsync(string? solutionPathOverride, CancellationToken cancellationToken)
    {
        // jb first, then the solution: a missing toolchain surfaces before any solution-discovery error.
        JbInstallation installation = await jbLocator.LocateAsync(cancellationToken);
        string solutionPath = ResolveSolutionPath(solutionPathOverride);
        SettingsResolution settings = ResolveSettingsPath(solutionPath);
        DeclaredCleanupProfile declaredProfile = CleanupProfileReader.Read(settings.Path);

        return new ResolvedConfig(
            solutionPath,
            settings.Path,
            declaredProfile.Name,
            ResolveCacheHome(),
            EmptyToNull(environment.GetVariable("JB_EXTENSIONS")),
            EmptyToNull(environment.GetVariable("JB_EXTENSION_SOURCE")),
            installation.ExecutablePath,
            new ConfigWarnings(settings.MissingEnvPath, declaredProfile.Failure));
    }

    private string ResolveSolutionPath(string? solutionPathOverride)
    {
        if (solutionPathOverride is not null)
        {
            string resolved = Path.GetFullPath(solutionPathOverride, environment.CurrentDirectory);
            if (!File.Exists(resolved)) throw new UserErrorException($"Specified solution path \"{solutionPathOverride}\" does not exist.");

            return resolved;
        }

        string? envPath = environment.GetVariable("JB_SOLUTION_PATH");
        if (!string.IsNullOrEmpty(envPath))
        {
            string resolved = Path.GetFullPath(envPath, environment.CurrentDirectory);
            if (!File.Exists(resolved)) throw new UserErrorException($"JB_SOLUTION_PATH is set to \"{envPath}\" but the file does not exist.");

            return resolved;
        }

        return DiscoverSolutionInCurrentDirectory();
    }

    private string DiscoverSolutionInCurrentDirectory()
    {
        string currentDirectory = environment.CurrentDirectory;

        // Top-level files only — no parent walk, and a directory named "Foo.sln" must not match.
        var solutionNames = Directory.EnumerateFiles(currentDirectory)
            .Select(Path.GetFileName)
            .Where(IsSolutionFileName)
            .Select(name => name!)
            .ToList();

        if (solutionNames.Count == 1) return Path.GetFullPath(Path.Combine(currentDirectory, solutionNames[0]));

        if (solutionNames.Count == 0)
            throw new UserErrorException(
                $"No .sln or .slnx file found in \"{currentDirectory}\".\n"
                + "Set the JB_SOLUTION_PATH environment variable to the full path of your solution file.");

        throw new UserErrorException(
            $"Multiple solution files found in \"{currentDirectory}\": {string.Join(", ", solutionNames)}.\n"
            + "Set the JB_SOLUTION_PATH environment variable to specify which one to use.");
    }

    private SettingsResolution ResolveSettingsPath(string solutionPath)
    {
        string? missingEnvPath = null;

        string? envPath = environment.GetVariable("JB_SETTINGS_PATH");
        if (!string.IsNullOrEmpty(envPath))
        {
            string resolved = Path.GetFullPath(envPath, environment.CurrentDirectory);
            if (File.Exists(resolved)) return new SettingsResolution(resolved, null);

            // Never throw on a bad settings path — warn and fall through to the other sources. Recorded as
            // well as logged: it silently drops both configuration axes, so the caller has to be told.
            Log.Warning("JB_SETTINGS_PATH is set to \"{EnvPath}\" but the file does not exist. Skipping.", envPath);
            missingEnvPath = envPath;
        }

        // Project-level {solution}.DotSettings next to the solution file.
        string solutionSettings = solutionPath + ".DotSettings";
        if (File.Exists(solutionSettings)) return new SettingsResolution(solutionSettings, missingEnvPath);

        // OS-specific JetBrains shared settings.
        string globalSettings = Path.Combine(SharedSettingsDirectory(), "GlobalSettingsStorage.DotSettings");
        if (File.Exists(globalSettings)) return new SettingsResolution(globalSettings, missingEnvPath);

        return new SettingsResolution(null, missingEnvPath);
    }

    private string SharedSettingsDirectory()
    {
        string home = environment.HomeDirectory;

        if (OperatingSystem.IsWindows())
        {
            string appData = environment.GetVariable("APPDATA") ?? Path.Combine(home, "AppData", "Roaming");
            return Path.Combine(appData, "JetBrains", "Shared", "vAny");
        }

        if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Application Support", "JetBrains", "Shared", "vAny");

        return Path.Combine(home, ".local", "share", "JetBrains", "Shared", "vAny");
    }

    private string ResolveCacheHome()
    {
        string? cacheHome = EmptyToNull(environment.GetVariable("JB_CACHE_HOME"));
        return cacheHome is not null
            ? Path.GetFullPath(cacheHome, environment.CurrentDirectory)
            : Path.Combine(environment.HomeDirectory, ".jb-cache");
    }

    private static bool IsSolutionFileName(string? name)
    {
        return name is not null
               && (name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    ///     The settings file the chain landed on, plus the <c>JB_SETTINGS_PATH</c> value that named a file
    ///     that does not exist. Both travel together because a bad env path does not stop the chain: it can
    ///     fall through to an adjacent or shared settings file, and the caller is owed the warning either way.
    /// </summary>
    private sealed record SettingsResolution(string? Path, string? MissingEnvPath);
}