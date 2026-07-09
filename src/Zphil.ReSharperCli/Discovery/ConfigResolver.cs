using Serilog;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Discovery;

/// <summary>Everything needed to shell out to <c>jb</c>: the solution, optional settings, cache home, and extensions.</summary>
internal sealed record ResolvedConfig(
    string SolutionPath,
    string? SettingsPath,
    string CacheHome,
    string? Extensions,
    string? ExtensionSource,
    string JbExecutablePath);

/// <summary>
///     Resolves the <see cref="ResolvedConfig" /> for a request: verifies <c>jb</c> is installed, then
///     locates the solution, settings, cache home, and extension defaults from overrides, environment
///     variables, and the current directory. A single-entry cache keyed by the solution override (or the
///     current directory) avoids re-probing on every tool call.
/// </summary>
internal sealed class ConfigResolver(JbLocator jbLocator, IEnvironment environment)
{
    // A record reference read/written atomically: a concurrent resolve can at worst redo the work, never
    // observe a torn (Key, Config) pair.
    private volatile CacheEntry? _cache;

    public async Task<ResolvedConfig> ResolveAsync(string? solutionPathOverride, CancellationToken cancellationToken)
    {
        string cacheKey = solutionPathOverride ?? environment.CurrentDirectory;
        if (_cache is { } cache && cache.Key == cacheKey) return cache.Config;

        // jb first, then the solution: a missing toolchain surfaces before any solution-discovery error.
        JbInstallation installation = await jbLocator.LocateAsync(cancellationToken);
        string solutionPath = ResolveSolutionPath(solutionPathOverride);

        ResolvedConfig config = new(
            solutionPath,
            ResolveSettingsPath(solutionPath),
            ResolveCacheHome(),
            EmptyToNull(environment.GetVariable("JB_EXTENSIONS")),
            EmptyToNull(environment.GetVariable("JB_EXTENSION_SOURCE")),
            installation.ExecutablePath);

        _cache = new CacheEntry(cacheKey, config);
        return config;
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

    private string? ResolveSettingsPath(string solutionPath)
    {
        string? envPath = environment.GetVariable("JB_SETTINGS_PATH");
        if (!string.IsNullOrEmpty(envPath))
        {
            string resolved = Path.GetFullPath(envPath, environment.CurrentDirectory);
            if (File.Exists(resolved)) return resolved;

            // Never throw on a bad settings path — warn and fall through to the other sources.
            Log.Warning("JB_SETTINGS_PATH is set to \"{EnvPath}\" but the file does not exist. Skipping.", envPath);
        }

        // Project-level {solution}.DotSettings next to the solution file.
        string solutionSettings = solutionPath + ".DotSettings";
        if (File.Exists(solutionSettings)) return solutionSettings;

        // OS-specific JetBrains shared settings.
        string globalSettings = Path.Combine(SharedSettingsDirectory(), "GlobalSettingsStorage.DotSettings");
        if (File.Exists(globalSettings)) return globalSettings;

        return null;
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

    /// <summary>The cache key (solution override or current directory) paired with the config it resolved to.</summary>
    private sealed record CacheEntry(string Key, ResolvedConfig Config);
}