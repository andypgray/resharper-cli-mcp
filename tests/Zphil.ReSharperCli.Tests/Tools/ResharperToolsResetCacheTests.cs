using NSubstitute;
using NSubstitute.Core;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Tests.Tools;

/// <summary>
///     <see cref="ResharperTools.ResetCacheAsync" /> end to end over the two faked seams, with the config and
///     service graph real.
/// </summary>
public sealed class ResharperToolsResetCacheTests : IDisposable
{
    private readonly string _cacheHome;
    private readonly FakeEnvironment _environment = new();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();

    public ResharperToolsResetCacheTests()
    {
        _cacheHome = _environment.CreateTempDirectory();
        _environment.SetVariable("JB_CACHE_HOME", _cacheHome);
        _environment.PlantSolution("App.sln");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public async Task ResetCacheAsync_SolutionWithACachedGeneration_DropsItAndReportsTheColdNextCall()
    {
        // Arrange — end to end through the tool method, including the discovery it shares with the other two.
        string ours = CacheHomes.PlantGenerationFor(_cacheHome, Path.Combine(_environment.CurrentDirectory, "App.sln"));
        CacheHomes.PlantGeneration(_cacheHome, "_Other.99.00");
        StubJb();
        ResharperTools tools = Tools();

        // Act
        string result = await tools.ResetCacheAsync(cancellationToken: Ct);

        // Assert
        result.ShouldContain("Dropped 1 ReSharper cache generation(s)");
        result.ShouldContain($"  - {Path.GetFileName(ours)}");
        result.ShouldEndWith("rebuilds the cache from cold, which can take minutes.");
        Directory.Exists(ours).ShouldBeFalse();
        Directory.Exists(Path.Combine(_cacheHome, "_Other.99.00")).ShouldBeTrue();
    }

    [Fact]
    public async Task ResetCacheAsync_SolutionPathThatNoLongerExists_ReclaimsItsGenerations()
    {
        // Arrange — a worktree deleted with its cache still on disk, which nothing else can name: the
        // generation is addressed by the hash of the path, and every other tool refuses a path with no file
        // on it. The live checkout's own generation sits in the same cache home under a name differing only
        // in the hash, so this also pins that the reclaim stays inside the path it was given.
        string removed = _environment.CreateSolutionPath("App.sln");
        string theirs = CacheHomes.PlantGenerationFor(_cacheHome, removed);
        string live = CacheHomes.PlantGenerationFor(_cacheHome, Path.Combine(_environment.CurrentDirectory, "App.sln"));
        StubJb();
        ResharperTools tools = Tools();

        // Act
        string result = await tools.ResetCacheAsync(removed, cancellationToken: Ct);

        // Assert — dropped, the live checkout's left alone and named, and no promise about a next call that
        // nobody can make.
        result.ShouldContain($"  - {Path.GetFileName(theirs)}");
        result.ShouldEndWith("seeded from a sibling checkout where one is warm.");
        Directory.Exists(theirs).ShouldBeFalse();
        Directory.Exists(live).ShouldBeTrue();
    }

    [Fact]
    public async Task ResetCacheAsync_RunsNoJbBeyondTheVersionProbe()
    {
        // Arrange — a reset is a directory delete, not an analysis. Spending a cold jb run here would double
        // the cost of the very situation the tool exists to get out of.
        StubJb();
        ResharperTools tools = Tools();

        // Act
        await tools.ResetCacheAsync(cancellationToken: Ct);

        // Assert — the version probe (which is itself an "inspectcode" invocation) and nothing else.
        await _processRunner.Received(1).AnyRunWith(args => args != null && args.Contains("--version"));
        await _processRunner.DidNotReceive().AnyRunWith(args => args != null && !args.Contains("--version"));
    }

    /// <summary>The tool methods over this class's environment and process runner.</summary>
    private ResharperTools Tools()
    {
        return ToolHarness.Build(_processRunner, _environment);
    }

    /// <summary>
    ///     Answers the version probe and succeeds every other run the way a healthy jb does, leaving its cache
    ///     generation behind — so a test that sees a run it did not expect sees it succeed, not throw.
    /// </summary>
    private void StubJb()
    {
        _processRunner
            .AnyRun()
            .Returns(Route);

        return;

        static ProcessResult Route(CallInfo callInfo)
        {
            IReadOnlyList<string> arguments = callInfo.Arguments();

            return JbStubs.IsVersionProbe(arguments) ? JbStubs.VersionProbeAnswer : JbStubs.Succeed(arguments);
        }
    }
}