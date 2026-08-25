using Microsoft.Extensions.Logging;
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
internal sealed record ConfigWarnings(string? MissingSettingsPath, SettingsReadFailure? SettingsRead)
{
    /// <summary>Nothing to report — the one spelling of "no warnings", so consumers never meet a null.</summary>
    public static readonly ConfigWarnings None = new(null, null);
}

/// <summary>Everything needed to shell out to <c>jb</c>: the solution, optional settings, cache home, and extensions.</summary>
/// <param name="SettingsPathIsCustomLayer">
///     Whether <see cref="SettingsPath" /> must ride <c>jb</c>'s command line as <c>--settings</c>: true only
///     when it is non-null <em>and</em> names a file outside every location <c>jb</c> mounts itself, so the
///     flag is reserved for the one case it exists for — a file <c>jb</c> cannot find on its own.
/// </param>
internal sealed record ResolvedConfig(
    string SolutionPath,
    string? SettingsPath,
    bool SettingsPathIsCustomLayer,
    string? CleanupProfile,
    string CacheHome,
    string? Extensions,
    string? ExtensionSource,
    string JbExecutablePath,
    ConfigWarnings Warnings)
{
    /// <summary>
    ///     The directory holding the solution — the root a relative <c>files</c> entry resolves against.
    ///     <see cref="SolutionPath" /> is always a resolved path to an existing file, so it always has one.
    /// </summary>
    public string SolutionDirectory => Path.GetDirectoryName(SolutionPath)!;
}

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
internal sealed class ConfigResolver(JbLocator jbLocator, IEnvironment environment, ILogger<ConfigResolver> logger)
{
    public async Task<ResolvedConfig> ResolveAsync(string? solutionPathOverride, CancellationToken cancellationToken)
    {
        // jb first, then the solution: a missing toolchain surfaces before any solution-discovery error.
        JbInstallation installation = await jbLocator.LocateAsync(cancellationToken);
        SolutionResolution solution = ResolveSolutionPath(solutionPathOverride);
        SettingsResolution settings = ResolveSettingsPath(solution.Path);
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
            new ConfigWarnings(settings.MissingEnvPath, declaredProfile.Failure));

        Report(config, solution.Source, installation.Version);

        return config;
    }

    /// <summary>
    ///     Where a solution's ReSharper caches live for this server process: <c>JB_CACHE_HOME</c> if set, else
    ///     <c>~/.jb-cache</c>.
    /// </summary>
    /// <remarks>
    ///     Internal so the startup line can name it without resolving a whole config — which would mean
    ///     probing for <c>jb</c>, thirty seconds per candidate on a machine that has none, before the server
    ///     has said anything at all. This one axis is independent of every other and costs two environment
    ///     reads.
    /// </remarks>
    internal string ResolveCacheHome()
    {
        string? cacheHome = EmptyToNull(environment.GetVariable("JB_CACHE_HOME"));
        return cacheHome is not null
            ? Path.GetFullPath(cacheHome, environment.CurrentDirectory)
            : Path.Combine(environment.HomeDirectory, ".jb-cache");
    }

    /// <summary>
    ///     Say what this call resolved and how. One line per call, at <c>Information</c>, because every axis on
    ///     it changes what <c>jb</c> is asked to do and none of them is visible from the outside: which of the
    ///     three solution sources won, and whether <c>--settings</c> mounts a Custom layer above the whole
    ///     stack, are exactly the two the 1.4.0 settings-layer defect lived on.
    /// </summary>
    private void Report(ResolvedConfig config, string solutionSource, string jbVersion)
    {
        logger.LogInformation(
            "Resolved solution {SolutionPath} ({SolutionSource}) against jb {JbVersion} at {JbPath}, cache home {CacheHome}, "
            + "settings {SettingsPath} ({SettingsLayer}), extensions {Extensions}",
            config.SolutionPath,
            solutionSource,
            jbVersion,
            config.JbExecutablePath,
            config.CacheHome,
            config.SettingsPath ?? "none found",
            config.SettingsPathIsCustomLayer ? "passed as --settings, mounting a Custom layer" : "left for jb to mount itself",
            config.Extensions ?? "none");
    }

    private SolutionResolution ResolveSolutionPath(string? solutionPathOverride)
    {
        if (solutionPathOverride is not null)
        {
            string resolved = Path.GetFullPath(solutionPathOverride, environment.CurrentDirectory);
            if (!File.Exists(resolved)) throw new UserErrorException($"Specified solution path \"{solutionPathOverride}\" does not exist.");

            return new SolutionResolution(resolved, "from the solutionPath argument");
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

        // Solution-level {solution}.DotSettings next to the solution file.
        string solutionSettings = solutionPath + ".DotSettings";
        if (File.Exists(solutionSettings)) return new SettingsResolution(solutionSettings, missingEnvPath, false);

        // OS-specific JetBrains shared settings.
        string globalSettings = GlobalSettingsPath();
        if (File.Exists(globalSettings)) return new SettingsResolution(globalSettings, missingEnvPath, false);

        return new SettingsResolution(null, missingEnvPath, false);
    }

    /// <summary>
    ///     Whether <c>jb</c> mounts this settings file itself — as its SolutionShared or GlobalAll layer.
    ///     Naming such a file with <c>--settings</c> does not add it; it re-mounts it as a Custom layer
    ///     <em>above</em> the project layers, so a <c>{project}.csproj.DotSettings</c> the solution relies on
    ///     stops applying. Only the <c>JB_SETTINGS_PATH</c> branch can land outside these two, which is the
    ///     one case <c>--settings</c> exists for.
    /// </summary>
    private bool JbMountsItself(string settingsPath, string solutionPath)
    {
        return PathsEqual(settingsPath, solutionPath + ".DotSettings")
               || PathsEqual(settingsPath, GlobalSettingsPath());
    }

    /// <summary>
    ///     How two settings paths are compared, matching the platform's filesystem case rules. Both operands
    ///     are already absolute and normalized; a symlinked or 8.3-form spelling defeats equality and simply
    ///     falls back to passing <c>--settings</c>.
    /// </summary>
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
    ///     The solution this call landed on, and which of the three sources supplied it. The source is carried
    ///     for the log alone — nothing about the run depends on it — which is why it stays a private detail
    ///     here rather than joining <see cref="ResolvedConfig" />.
    /// </summary>
    private sealed record SolutionResolution(string Path, string Source);

    /// <summary>
    ///     The settings file the chain landed on, plus the <c>JB_SETTINGS_PATH</c> value that named a file
    ///     that does not exist. Both travel together because a bad env path does not stop the chain: it can
    ///     fall through to an adjacent or shared settings file, and the caller is owed the warning either way.
    ///     <see cref="IsCustomLayer" /> can be true only for the env branch — the other two land on files
    ///     <c>jb</c> mounts itself.
    /// </summary>
    private sealed record SettingsResolution(string? Path, string? MissingEnvPath, bool IsCustomLayer);
}