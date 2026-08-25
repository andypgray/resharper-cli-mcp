using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Discovery;

public sealed class ConfigResolverTests : IDisposable
{
    private readonly FakeEnvironment _environment = new();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly ConfigResolver _resolver;

    public ConfigResolverTests()
    {
        _processRunner
            .RunAsync("jb", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, "Version: 2026.1.2\n", string.Empty));
        _resolver = new ConfigResolver(
            new JbLocator(_processRunner, _environment, NullLogger<JbLocator>.Instance),
            _environment,
            NullLogger<ConfigResolver>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _environment.Dispose();
    }

    // ── Solution: override ────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_SolutionOverrideExists_UsesResolvedOverride()
    {
        // Arrange
        string overridePath = CreateSolutionInCurrentDirectory("Explicit.sln");

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(overridePath, Ct);

        // Assert
        config.SolutionPath.ShouldBe(Path.GetFullPath(overridePath));
    }

    [Fact]
    public async Task ResolveAsync_SolutionOverrideMissing_ThrowsExactMessage()
    {
        // Arrange
        string missing = Path.Combine(_environment.CurrentDirectory, "Nope.sln");

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _resolver.ResolveAsync(missing, Ct));

        // Assert
        exception.Message.ShouldBe($"Specified solution path \"{missing}\" does not exist.");
    }

    // ── Solution: JB_SOLUTION_PATH ────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_JbSolutionPathEnvExists_UsesIt()
    {
        // Arrange
        string sln = Path.Combine(_environment.CreateTempDirectory(), "Env.sln");
        File.WriteAllText(sln, string.Empty);
        _environment.SetVariable("JB_SOLUTION_PATH", sln);

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.SolutionPath.ShouldBe(Path.GetFullPath(sln));
    }

    [Fact]
    public async Task ResolveAsync_JbSolutionPathEnvSetButMissing_ThrowsExactMessage()
    {
        // Arrange
        string missing = Path.Combine(_environment.CurrentDirectory, "Ghost.sln");
        _environment.SetVariable("JB_SOLUTION_PATH", missing);

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _resolver.ResolveAsync(null, Ct));

        // Assert
        exception.Message.ShouldBe($"JB_SOLUTION_PATH is set to \"{missing}\" but the file does not exist.");
    }

    // ── Solution: current-directory scan ──────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_SingleSlnInCurrentDirectory_UsesIt()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("Only.sln");

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.SolutionPath.ShouldEndWith("Only.sln");
    }

    [Fact]
    public async Task ResolveAsync_SingleSlnxInCurrentDirectory_IsRecognized()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("Modern.slnx");

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.SolutionPath.ShouldEndWith("Modern.slnx");
    }

    [Fact]
    public async Task ResolveAsync_NoSolutionInCurrentDirectory_ThrowsWithHint()
    {
        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _resolver.ResolveAsync(null, Ct));

        // Assert
        exception.Message.ShouldBe(
            $"No .sln or .slnx file found in \"{_environment.CurrentDirectory}\".\n"
            + "Set the JB_SOLUTION_PATH environment variable to the full path of your solution file.");
    }

    [Fact]
    public async Task ResolveAsync_DirectoryNamedLikeSolutionAlongsideRealSolution_ResolvesTheRealFile()
    {
        // Arrange — a *directory* named "Fake.sln" must not be counted as a solution file.
        CreateSolutionInCurrentDirectory("App.sln");
        Directory.CreateDirectory(Path.Combine(_environment.CurrentDirectory, "Fake.sln"));

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.SolutionPath.ShouldEndWith("App.sln");
    }

    [Fact]
    public async Task ResolveAsync_MultipleSolutionsInCurrentDirectory_ThrowsListingNamesNotPaths()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("Alpha.sln");
        CreateSolutionInCurrentDirectory("Beta.slnx");

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _resolver.ResolveAsync(null, Ct));

        // Assert
        exception.Message.ShouldContain("Multiple solution files found in");
        exception.Message.ShouldContain("Alpha.sln");
        exception.Message.ShouldContain("Beta.slnx");
        exception.Message.ShouldContain("Set the JB_SOLUTION_PATH environment variable to specify which one to use.");
        // Names, not full paths.
        exception.Message.ShouldNotContain(Path.Combine(_environment.CurrentDirectory, "Alpha.sln"));
    }

    [Fact]
    public async Task ResolveAsync_SolutionOnlyInParentDirectory_StillThrows()
    {
        // Arrange
        string parent = _environment.CreateTempDirectory();
        File.WriteAllText(Path.Combine(parent, "Parent.sln"), string.Empty);
        string child = Path.Combine(parent, "child");
        Directory.CreateDirectory(child);
        _environment.CurrentDirectory = child;

        // Act
        var exception = await Should.ThrowAsync<UserErrorException>(() => _resolver.ResolveAsync(null, Ct));

        // Assert
        exception.Message.ShouldContain("No .sln or .slnx file found in");
    }

    // ── Settings chain ────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_JbSettingsPathEnvExists_UsesIt()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("App.sln");
        string settings = Path.Combine(_environment.CreateTempDirectory(), "Custom.DotSettings");
        File.WriteAllText(settings, string.Empty);
        _environment.SetVariable("JB_SETTINGS_PATH", settings);

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.SettingsPath.ShouldBe(Path.GetFullPath(settings));
    }

    [Fact]
    public async Task ResolveAsync_JbSettingsPathEnvMissing_WarnsAndFallsThroughToNull()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("App.sln");
        _environment.SetVariable("JB_SETTINGS_PATH", Path.Combine(_environment.CurrentDirectory, "missing.DotSettings"));

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert  (a bad settings path never throws)
        config.SettingsPath.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_AdjacentDotSettingsExists_IsPreferred()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("App.sln");
        File.WriteAllText(Path.Combine(_environment.CurrentDirectory, "App.sln.DotSettings"), string.Empty);

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.SettingsPath.ShouldBe(config.SolutionPath + ".DotSettings");
    }

    [Fact]
    public async Task ResolveAsync_OnlySharedSettingsExist_UsesSharedSettings()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("App.sln");
        string sharedSettings = WriteSharedGlobalSettings();

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.SettingsPath.ShouldBe(sharedSettings);
    }

    [Fact]
    public async Task ResolveAsync_NoSettingsAnywhere_ReturnsNull()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("App.sln");

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.SettingsPath.ShouldBeNull();
        config.SettingsPathIsCustomLayer.ShouldBeFalse();
    }

    // ── Settings: the custom-layer split ──────────────────────────────────────
    // jb mounts the adjacent {solution}.DotSettings (SolutionShared) and the shared
    // GlobalSettingsStorage.DotSettings (GlobalAll) itself, so naming either as --settings would not be a
    // no-op: a Custom layer sits above the project layers, and every {project}.csproj.DotSettings in the
    // solution would silently stop applying. Only a JB_SETTINGS_PATH outside those two earns the flag.

    [Fact]
    public async Task ResolveAsync_JbSettingsPathNamesAFileJbCannotDiscover_IsACustomLayer()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("App.sln");
        string settings = Path.Combine(_environment.CreateTempDirectory(), "Custom.DotSettings");
        File.WriteAllText(settings, string.Empty);
        _environment.SetVariable("JB_SETTINGS_PATH", settings);

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert — the one case --settings exists for: jb has no way to find this file on its own.
        config.SettingsPathIsCustomLayer.ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_AdjacentDotSettings_IsNotACustomLayer()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("App.sln");
        WriteAdjacentSettings(string.Empty);

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.SettingsPath.ShouldNotBeNull();
        config.SettingsPathIsCustomLayer.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_SharedGlobalSettings_IsNotACustomLayer()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("App.sln");
        WriteSharedGlobalSettings();

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert — the worse instance of the same demotion: a personal, machine-wide IDE preference
        // mounted above a repo's checked-in project settings.
        config.SettingsPath.ShouldNotBeNull();
        config.SettingsPathIsCustomLayer.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_JbSettingsPathNamesTheAdjacentDotSettings_IsNotACustomLayer()
    {
        // Arrange — hardening: what the env var names is what jb would discover anyway, so passing it
        // as --settings would demote the project layers exactly as the discovered branch would.
        CreateSolutionInCurrentDirectory("App.sln");
        WriteAdjacentSettings(string.Empty);
        _environment.SetVariable("JB_SETTINGS_PATH", Path.Combine(_environment.CurrentDirectory, "App.sln.DotSettings"));

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.SettingsPath.ShouldBe(config.SolutionPath + ".DotSettings");
        config.SettingsPathIsCustomLayer.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_JbSettingsPathNamesTheSharedGlobalSettings_IsNotACustomLayer()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("App.sln");
        string sharedSettings = WriteSharedGlobalSettings();
        _environment.SetVariable("JB_SETTINGS_PATH", sharedSettings);

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.SettingsPath.ShouldBe(sharedSettings);
        config.SettingsPathIsCustomLayer.ShouldBeFalse();
    }

    // ── Cleanup profile ───────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_SettingsDeclareSilentCleanupProfile_ResolvesIt()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("App.sln");
        WriteAdjacentSettings(DotSettingsFixtures.Declaring("House: Keep Named Arguments"));

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.CleanupProfile.ShouldBe("House: Keep Named Arguments");
    }

    [Fact]
    public async Task ResolveAsync_SettingsWithoutSilentCleanupProfile_ResolvesNullProfile()
    {
        // Arrange — a settings file that tunes something else entirely.
        CreateSolutionInCurrentDirectory("App.sln");
        WriteAdjacentSettings(
            """
            <wpf:ResourceDictionary xml:space="preserve" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:s="clr-namespace:System;assembly=mscorlib" xmlns:wpf="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            	<s:String x:Key="/Default/CodeInspection/Highlighting/InspectionSeverities/=RedundantCast/@EntryIndexedValue">DO_NOT_SHOW</s:String>
            </wpf:ResourceDictionary>
            """);

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.CleanupProfile.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_SilentCleanupProfileIsBlank_ResolvesNullProfile()
    {
        // Arrange — a blank name would reach jb as --profile= and fail the run; it must read as "unset".
        CreateSolutionInCurrentDirectory("App.sln");
        WriteAdjacentSettings(DotSettingsFixtures.Declaring("   "));

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.CleanupProfile.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_MalformedSettingsFile_ResolvesNullProfileWithoutThrowing()
    {
        // Arrange — a settings file this server cannot parse must degrade to the built-in default, not
        // fail every cleanup call.
        CreateSolutionInCurrentDirectory("App.sln");
        WriteAdjacentSettings(DotSettingsFixtures.Unparseable());

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.CleanupProfile.ShouldBeNull();
        config.SettingsPath.ShouldNotBeNull(); // jb reads the file itself and has its own opinion of it
    }

    [Fact]
    public async Task ResolveAsync_SettingsDeclareProfileBehindAnIllegalComment_StillResolvesIt()
    {
        // Arrange — the field failure: a comment containing `--` is illegal XML but ReSharper and jb read
        // the file happily, so rejecting it here turned the declared-profile feature off without a word.
        CreateSolutionInCurrentDirectory("App.sln");
        WriteAdjacentSettings(DotSettingsFixtures.DeclaringBehindIllegalComment("House: Keep Named Arguments"));

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.CleanupProfile.ShouldBe("House: Keep Named Arguments");
        config.Warnings.SettingsRead.ShouldBeNull(); // recovered, so there is nothing to report
    }

    [Fact]
    public async Task ResolveAsync_NoSettingsAnywhere_ResolvesNullProfile()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("App.sln");

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.CleanupProfile.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_SettingsPathContainsUriMetaCharacters_StillReadsTheProfile()
    {
        // Arrange — '%' and '#' are URI metacharacters and legal in a path. Reading the settings file
        // through a stream keeps them a non-issue; this pins that, so a future switch back to a
        // URI-resolving overload cannot make a declared profile silently read as unset on some platform.
        string awkwardDirectory = Path.Combine(_environment.CreateTempDirectory(), "100%#done");
        Directory.CreateDirectory(awkwardDirectory);
        _environment.CurrentDirectory = awkwardDirectory;
        CreateSolutionInCurrentDirectory("App.sln");
        WriteAdjacentSettings(DotSettingsFixtures.Declaring("House: Keep Named Arguments"));

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.CleanupProfile.ShouldBe("House: Keep Named Arguments");
    }

    // ── Warnings ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_UnreadableSettingsFile_RecordsTheReadFailureAsAWarning()
    {
        // Arrange — the fallback to Full Cleanup rewrites the code the declared profile was protecting, so
        // the failure has to reach the caller and not just the log.
        CreateSolutionInCurrentDirectory("App.sln");
        WriteAdjacentSettings(DotSettingsFixtures.Unparseable());

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.Warnings.ShouldNotBeNull();
        config.Warnings.SettingsRead.ShouldNotBeNull();
        config.Warnings.SettingsRead.Path.ShouldBe(config.SettingsPath);
        config.Warnings.MissingSettingsPath.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_JbSettingsPathEnvMissing_RecordsThePathAsAWarning()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("App.sln");
        string missing = Path.Combine(_environment.CurrentDirectory, "missing.DotSettings");
        _environment.SetVariable("JB_SETTINGS_PATH", missing);

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert — the value as set, matching the log line and what the user has to go and fix.
        config.Warnings.ShouldNotBeNull();
        config.Warnings.MissingSettingsPath.ShouldBe(missing);
        config.Warnings.SettingsRead.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_JbSettingsPathEnvMissingButAdjacentSettingsExist_StillWarnsAboutTheEnvPath()
    {
        // Arrange — a bad env path does not stop the chain, so the run is configured by a file the user did
        // not name. Silently substituting one settings file for another is still worth saying out loud.
        CreateSolutionInCurrentDirectory("App.sln");
        WriteAdjacentSettings(DotSettingsFixtures.Declaring("House: Keep Named Arguments"));
        string missing = Path.Combine(_environment.CurrentDirectory, "missing.DotSettings");
        _environment.SetVariable("JB_SETTINGS_PATH", missing);

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.SettingsPath.ShouldBe(config.SolutionPath + ".DotSettings");
        config.CleanupProfile.ShouldBe("House: Keep Named Arguments");
        config.Warnings.ShouldNotBeNull();
        config.Warnings.MissingSettingsPath.ShouldBe(missing);
    }

    [Fact]
    public async Task ResolveAsync_EverythingResolvesCleanly_RecordsNoWarnings()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("App.sln");
        WriteAdjacentSettings(DotSettingsFixtures.Declaring("House: Keep Named Arguments"));

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert — the ordinary case must stay silent; a banner on every call would train an agent to skip it.
        config.Warnings.ShouldNotBeNull();
        config.Warnings.MissingSettingsPath.ShouldBeNull();
        config.Warnings.SettingsRead.ShouldBeNull();
    }

    // ── Cache home + extensions ───────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_NoCacheHomeEnv_DefaultsToDotJbCacheUnderHome()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("App.sln");

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.CacheHome.ShouldBe(Path.Combine(_environment.HomeDirectory, ".jb-cache"));
    }

    [Fact]
    public async Task ResolveAsync_JbCacheHomeEnvSet_UsesIt()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("App.sln");
        string cache = _environment.CreateTempDirectory();
        _environment.SetVariable("JB_CACHE_HOME", cache);

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.CacheHome.ShouldBe(cache);
    }

    [Fact]
    public async Task ResolveAsync_JbCacheHomeEnvEmpty_DefaultsToDotJbCacheUnderHome()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("App.sln");
        _environment.SetVariable("JB_CACHE_HOME", string.Empty);

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert  (empty is treated as unset, not as the current directory)
        config.CacheHome.ShouldBe(Path.Combine(_environment.HomeDirectory, ".jb-cache"));
    }

    [Fact]
    public async Task ResolveAsync_JbCacheHomeEnvRelative_IsAnchoredUnderCurrentDirectory()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("App.sln");
        _environment.SetVariable("JB_CACHE_HOME", "relative-cache");

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.CacheHome.ShouldBe(Path.Combine(_environment.CurrentDirectory, "relative-cache"));
    }

    [Fact]
    public async Task ResolveAsync_JbExtensionsEmptyString_ResolvesToNull()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("App.sln");
        _environment.SetVariable("JB_EXTENSIONS", string.Empty);

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.Extensions.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_JbExtensionsAndSourceSet_UsesValues()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("App.sln");
        _environment.SetVariable("JB_EXTENSIONS", "Foo.Plugin;Bar.Plugin");
        _environment.SetVariable("JB_EXTENSION_SOURCE", "https://example.test/nuget");

        // Act
        ResolvedConfig config = await _resolver.ResolveAsync(null, Ct);

        // Assert
        config.Extensions.ShouldBe("Foo.Plugin;Bar.Plugin");
        config.ExtensionSource.ShouldBe("https://example.test/nuget");
    }

    // ── Re-resolution ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_SettingsWrittenAfterAnEarlierResolve_AreSeenOnTheNextCall()
    {
        // Arrange — the reason the resolver holds no cache. An agent that declares a cleanup profile
        // mid-session (exactly what the configuration guide tells it to do) must not keep getting Full
        // Cleanup — the very rewrite the profile was defined to prevent — until the client restarts us.
        CreateSolutionInCurrentDirectory("App.sln");
        ResolvedConfig before = await _resolver.ResolveAsync(null, Ct);
        before.CleanupProfile.ShouldBeNull();

        WriteAdjacentSettings(DotSettingsFixtures.Declaring("House: Keep Named Arguments"));

        // Act
        ResolvedConfig after = await _resolver.ResolveAsync(null, Ct);

        // Assert
        after.CleanupProfile.ShouldBe("House: Keep Named Arguments");
        after.SettingsPath.ShouldBe(after.SolutionPath + ".DotSettings");
    }

    [Fact]
    public async Task ResolveAsync_CalledRepeatedly_ProbesJbOnlyOnce()
    {
        // Arrange — resolving fresh every call must not mean re-probing jb. That probe is the one
        // genuinely expensive step, and JbLocator caches it for the process; the rest is a directory
        // enumeration and a small XML read.
        CreateSolutionInCurrentDirectory("App.sln");

        // Act
        await _resolver.ResolveAsync(null, Ct);
        await _resolver.ResolveAsync(null, Ct);
        await _resolver.ResolveAsync(null, Ct);

        // Assert
        await _processRunner.Received(1).RunAsync(
            "jb", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_DifferentCurrentDirectory_ResolvesTheOtherSolution()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("First.sln");
        ResolvedConfig first = await _resolver.ResolveAsync(null, Ct);

        string secondDirectory = _environment.CreateTempDirectory();
        File.WriteAllText(Path.Combine(secondDirectory, "Second.sln"), string.Empty);
        _environment.CurrentDirectory = secondDirectory;

        // Act
        ResolvedConfig second = await _resolver.ResolveAsync(null, Ct);

        // Assert
        first.SolutionPath.ShouldEndWith("First.sln");
        second.SolutionPath.ShouldEndWith("Second.sln");
    }

    private void WriteAdjacentSettings(string content)
    {
        File.WriteAllText(Path.Combine(_environment.CurrentDirectory, "App.sln.DotSettings"), content);
    }

    private string WriteSharedGlobalSettings()
    {
        string sharedSettings = Path.Combine(ExpectedSharedSettingsDirectory(), "GlobalSettingsStorage.DotSettings");
        Directory.CreateDirectory(Path.GetDirectoryName(sharedSettings)!);
        File.WriteAllText(sharedSettings, string.Empty);
        return sharedSettings;
    }

    private string CreateSolutionInCurrentDirectory(string fileName)
    {
        string path = Path.Combine(_environment.CurrentDirectory, fileName);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private string ExpectedSharedSettingsDirectory()
    {
        string home = _environment.HomeDirectory;

        if (OperatingSystem.IsWindows())
        {
            string appData = _environment.GetVariable("APPDATA") ?? Path.Combine(home, "AppData", "Roaming");
            return Path.Combine(appData, "JetBrains", "Shared", "vAny");
        }

        if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Application Support", "JetBrains", "Shared", "vAny");

        return Path.Combine(home, ".local", "share", "JetBrains", "Shared", "vAny");
    }
}