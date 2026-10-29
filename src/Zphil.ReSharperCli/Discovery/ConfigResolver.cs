using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Discovery;

/// <summary>
///     Represents what went wrong while resolving configuration, in a form a tool can report rather than only
///     log.
/// </summary>
/// <remarks>
///     Neither fails a call. Both degrade it silently, which is why they are reported, and their blast radii
///     differ: <see cref="MissingSettingsPath" /> means the settings file the user named was never applied,
///     taking inspection severities and cleanup profiles with it, while <see cref="SettingsRead" /> means
///     <c>jb</c> was still given the file and only this server's own profile lookup failed, so cleanup fell
///     back to a broader profile.
/// </remarks>
internal sealed record ConfigWarnings(string? MissingSettingsPath, SettingsReadFailure? SettingsRead);

/// <summary>Represents everything needed to shell out to <c>jb</c>.</summary>
/// <remarks>
///     <paramref name="SolutionPath" /> and <paramref name="CacheHome" /> are always resolved absolute.
///     <paramref name="SettingsPathIsCustomLayer" /> says whether <paramref name="SettingsPath" /> rides
///     <c>jb</c>'s command line as <c>--settings</c>: true only when it names a file outside every location
///     <c>jb</c> mounts itself, a file <c>jb</c> cannot find on its own. <paramref name="JbVersion" /> is what
///     the located <c>jb</c> reported itself as, and reaches no command line; it is nullable although
///     <see cref="JbInstallation.Version" /> is not, because "no build known" turns the staleness judgement
///     off.
/// </remarks>
internal sealed record ResolvedConfig(
    string SolutionPath,
    string? SettingsPath,
    bool SettingsPathIsCustomLayer,
    string? CleanupProfile,
    string CacheHome,
    string? Extensions,
    string? ExtensionSource,
    string JbExecutablePath,
    ConfigWarnings Warnings,
    string? JbVersion = null)
{
    /// <summary>
    ///     Gets the directory holding the solution, the root a relative <c>files</c> entry resolves against.
    /// </summary>
    /// <remarks>
    ///     For a solution filter, the filter's own directory rather than the solution's, because that is where
    ///     <c>jb</c> anchors <c>--include</c>. Never null, because <see cref="SolutionPath" /> is always absolute.
    ///     The directory exists for every config the analysis tools see; a config from
    ///     <see cref="ConfigResolver.ResolveForCacheResetAsync" /> may name the solution of a deleted checkout,
    ///     whose directory need not exist.
    /// </remarks>
    public string SolutionDirectory => Path.GetDirectoryName(SolutionPath)!;
}

/// <summary>
///     Resolves the <see cref="ResolvedConfig" /> for a request: verifies <c>jb</c> is installed, then
///     locates the solution, settings, declared cleanup profile, cache home, and extension defaults from
///     overrides, environment variables, and the current directory.
/// </summary>
/// <remarks>
///     Uncached apart from the <c>jb</c> probe <see cref="JbLocator" /> caches, so a settings file added or
///     edited mid-session takes effect on the next call instead of after a client restart; the configuration
///     guide relies on that when it has an agent declare a cleanup profile. What is re-read on each call is
///     cheap beside the <c>jb</c> run that follows: a directory enumeration, a few existence checks and a
///     small XML read.
/// </remarks>
internal sealed class ConfigResolver(JbLocator jbLocator, IEnvironment environment, ILogger<ConfigResolver> logger)
{
    public Task<ResolvedConfig> ResolveAsync(string? solutionPathOverride, CancellationToken cancellationToken)
    {
        return ResolveAsync(solutionPathOverride, false, cancellationToken);
    }

    /// <summary>
    ///     Resolves the same configuration for a cache reset, which addresses cache directories by the hash of
    ///     the solution path and so needs the path rather than the file.
    /// </summary>
    /// <remarks>
    ///     An explicit <paramref name="solutionPathOverride" /> naming no existing file resolves anyway, which
    ///     is what lets the cache a deleted worktree or clone left behind be named and reclaimed. Only that
    ///     branch is relaxed. <c>JB_SOLUTION_PATH</c> pointing at a file that is not there is a misconfigured
    ///     server, and working-directory discovery cannot conjure a path out of nothing, so both still fail.
    ///     <c>jb</c> is still located first, so every tool asks the same of an installation.
    /// </remarks>
    public Task<ResolvedConfig> ResolveForCacheResetAsync(string? solutionPathOverride, CancellationToken cancellationToken)
    {
        return ResolveAsync(solutionPathOverride, true, cancellationToken);
    }

    private async Task<ResolvedConfig> ResolveAsync(
        string? solutionPathOverride,
        bool overrideMayBeMissing,
        CancellationToken cancellationToken)
    {
        // jb first, then the solution: a missing toolchain surfaces before any solution-discovery error.
        JbInstallation installation = await jbLocator.LocateAsync(cancellationToken);
        SolutionResolution solution = ResolveSolutionPath(solutionPathOverride, overrideMayBeMissing);

        // jb mounts the settings of the solution a filter names, so the chain anchors there too. A reset runs no
        // jb and reads no settings that matter to it, and may be reclaiming a filter that is no longer on disk.
        string settingsAnchor = SolutionFilterFile.IsFilter(solution.Path) && !overrideMayBeMissing
            ? SolutionFilterFile.ReadSolutionPath(solution.Path)
            : solution.Path;
        SettingsResolution settings = ResolveSettingsPath(settingsAnchor);
        DeclaredCleanupProfile declaredProfile = CleanupProfileReader.Read(settings.Path, logger);

        ResolvedConfig config = new(
            solution.Path,
            settings.Path,
            settings.IsCustomLayer,
            declaredProfile.Name,
            ResolveCacheHome(),
            EmptyToNull(environment.GetVariable("JB_EXTENSIONS")),
            EmptyToNull(environment.GetVariable("JB_EXTENSION_SOURCE")),
            installation.ExecutablePath,
            new ConfigWarnings(settings.MissingEnvPath, declaredProfile.Failure),
            installation.Version);

        Report(config, solution.Source);

        return config;
    }

    /// <summary>
    ///     Returns where a solution's ReSharper caches live for this server process: <c>JB_CACHE_HOME</c> if
    ///     set, else <c>~/.jb-cache</c>.
    /// </summary>
    /// <remarks>
    ///     Readable without resolving a whole config, which would mean probing for <c>jb</c>: a probe timeout
    ///     per candidate on a machine that has none.
    /// </remarks>
    internal string ResolveCacheHome()
    {
        string? cacheHome = EmptyToNull(environment.GetVariable("JB_CACHE_HOME"));
        return cacheHome is not null
            ? Path.GetFullPath(cacheHome, environment.CurrentDirectory)
            : Path.Combine(environment.HomeDirectory, ".jb-cache");
    }

    /// <summary>Logs what this call resolved and how.</summary>
    /// <remarks>
    ///     One line per call, at <c>Information</c>, because every axis on it changes what <c>jb</c> is asked
    ///     to do and none of them is visible from outside: above all which solution source won, and whether
    ///     <c>--settings</c> mounts a Custom layer above the whole stack.
    /// </remarks>
    private void Report(ResolvedConfig config, string solutionSource)
    {
        logger.LogInformation(
            "Resolved solution {SolutionPath} ({SolutionSource}) against jb {JbVersion} at {JbPath}, cache home {CacheHome}, "
            + "settings {SettingsPath} ({SettingsLayer}), extensions {Extensions}",
            config.SolutionPath,
            solutionSource,
            config.JbVersion,
            config.JbExecutablePath,
            config.CacheHome,
            config.SettingsPath ?? "none found",
            config.SettingsPathIsCustomLayer ? "passed as --settings, mounting a Custom layer" : "left for jb to mount itself",
            config.Extensions ?? "none");
    }

    private SolutionResolution ResolveSolutionPath(string? solutionPathOverride, bool overrideMayBeMissing)
    {
        if (solutionPathOverride is not null)
        {
            // Resolved against the working directory on both branches, so the string a reclaim hashes is the
            // string a run of that checkout would have hashed.
            string resolved = Path.GetFullPath(solutionPathOverride, environment.CurrentDirectory);
            if (File.Exists(resolved)) return new SolutionResolution(resolved, "from the solutionPath argument");

            if (!overrideMayBeMissing) throw new UserErrorException($"Specified solution path \"{solutionPathOverride}\" does not exist.");

            return new SolutionResolution(resolved, "from the solutionPath argument, which names no existing file");
        }

        string? envPath = environment.GetVariable("JB_SOLUTION_PATH");
        if (!string.IsNullOrEmpty(envPath))
        {
            string resolved = Path.GetFullPath(envPath, environment.CurrentDirectory);
            if (!File.Exists(resolved)) throw new UserErrorException($"JB_SOLUTION_PATH is set to \"{envPath}\" but the file does not exist.");

            return new SolutionResolution(resolved, "from JB_SOLUTION_PATH");
        }

        return new SolutionResolution(DiscoverSolutionInCurrentDirectory(), "discovered in the working directory");
    }

    private string DiscoverSolutionInCurrentDirectory()
    {
        string currentDirectory = environment.CurrentDirectory;

        // Top-level files only — no parent walk, and a directory named "Foo.sln" must not match.
        List<string> solutionNames = Directory.EnumerateFiles(currentDirectory)
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
            if (File.Exists(resolved)) return new SettingsResolution(resolved, null, !JbMountsItself(resolved, solutionPath));

            // Never throw on a bad settings path — warn and fall through to the other sources. Recorded as
            // well as logged: it silently drops both configuration axes, so the caller has to be told.
            logger.LogWarning("JB_SETTINGS_PATH is set to \"{EnvPath}\" but the file does not exist. Skipping.", envPath);
            missingEnvPath = envPath;
        }

        string solutionSettings = solutionPath + ".DotSettings";
        if (File.Exists(solutionSettings)) return new SettingsResolution(solutionSettings, missingEnvPath, false);

        string globalSettings = GlobalSettingsPath();
        if (File.Exists(globalSettings)) return new SettingsResolution(globalSettings, missingEnvPath, false);

        return new SettingsResolution(null, missingEnvPath, false);
    }

    /// <summary>
    ///     Determines whether <c>jb</c> mounts this settings file itself, as its SolutionShared or GlobalAll
    ///     layer.
    /// </summary>
    /// <remarks>
    ///     Naming such a file with <c>--settings</c> does not add it; it re-mounts it as a Custom layer above
    ///     the project layers, so a <c>{project}.csproj.DotSettings</c> the solution relies on stops applying.
    ///     Only the <c>JB_SETTINGS_PATH</c> branch can land outside those layers, which is the one case
    ///     <c>--settings</c> exists for.
    /// </remarks>
    private bool JbMountsItself(string settingsPath, string solutionPath)
    {
        return PathsEqual(settingsPath, solutionPath + ".DotSettings")
               || PathsEqual(settingsPath, GlobalSettingsPath());
    }

    /// <summary>
    ///     Compares two settings paths, with the operating system standing in for the filesystem's case rules.
    /// </summary>
    /// <remarks>
    ///     Both operands are already absolute and normalized. A symlinked or 8.3-form spelling, or a
    ///     case-insensitive filesystem the operating system key misreads, defeats equality and falls back to
    ///     passing <c>--settings</c>.
    /// </remarks>
    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private string GlobalSettingsPath()
    {
        return Path.Combine(SharedSettingsDirectory(), "GlobalSettingsStorage.DotSettings");
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
    ///     Represents the solution this call landed on, and which source supplied it.
    /// </summary>
    /// <remarks>
    ///     The source is carried for the log alone, so it stays here rather than joining
    ///     <see cref="ResolvedConfig" />.
    /// </remarks>
    private sealed record SolutionResolution(string Path, string Source);

    /// <summary>
    ///     Represents the settings file the chain landed on, plus the <c>JB_SETTINGS_PATH</c> value that named
    ///     a file that does not exist.
    /// </summary>
    /// <remarks>
    ///     Both travel together because a bad env path does not stop the chain: it can fall through to an
    ///     adjacent or shared settings file, and the caller is owed the warning either way.
    ///     <see cref="IsCustomLayer" /> can be true only for the env branch, since the others land on files
    ///     <c>jb</c> mounts itself.
    /// </remarks>
    private sealed record SettingsResolution(string? Path, string? MissingEnvPath, bool IsCustomLayer);
}