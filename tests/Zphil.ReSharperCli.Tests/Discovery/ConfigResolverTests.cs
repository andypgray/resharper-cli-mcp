using NSubstitute;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;

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
        _resolver = new ConfigResolver(new JbLocator(_processRunner, _environment), _environment);
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
        string sharedSettings = Path.Combine(ExpectedSharedSettingsDirectory(), "GlobalSettingsStorage.DotSettings");
        Directory.CreateDirectory(Path.GetDirectoryName(sharedSettings)!);
        File.WriteAllText(sharedSettings, string.Empty);

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

    // ── Caching ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_SameCurrentDirectoryTwice_ReturnsCachedInstance()
    {
        // Arrange
        CreateSolutionInCurrentDirectory("App.sln");

        // Act
        ResolvedConfig first = await _resolver.ResolveAsync(null, Ct);
        ResolvedConfig second = await _resolver.ResolveAsync(null, Ct);

        // Assert
        second.ShouldBeSameAs(first);
    }

    [Fact]
    public async Task ResolveAsync_DifferentCurrentDirectory_ReResolves()
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
        second.ShouldNotBeSameAs(first);
        first.SolutionPath.ShouldEndWith("First.sln");
        second.SolutionPath.ShouldEndWith("Second.sln");
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