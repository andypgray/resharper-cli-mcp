using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Serilog.Events;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Infrastructure;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Infrastructure;

/// <summary>
///     The two lines that bracket a server process, and the fields the first of them exists for.
/// </summary>
/// <remarks>
///     Every field on the fingerprint was a question the log could not answer, and the run cap is the one that
///     motivated it: an operator who set <c>RESHARPER_MCP_TIMEOUT_SECS</c> in a client config had no way to
///     confirm the server ever read it, and a value the client did not pass through looked exactly like one
///     that was ignored. So the assertion is that the cap <em>reported</em> is the cap the composition root
///     resolved, not merely that a line was written.
/// </remarks>
public sealed class ServerLifecycleTests : IDisposable
{
    private readonly FakeEnvironment _environment = new();
    private readonly CapturingLoggerProvider _logs = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public async Task StartAsync_ReportsTheConfigurationTheProcessIsActuallyRunningUnder()
    {
        // Arrange — a cap that is neither the default nor a round number, and a level that is not the
        // fallback, so a line that quietly reported either default instead would fail rather than coincide.
        string cacheHome = _environment.CreateTempDirectory();
        _environment.SetVariable("JB_CACHE_HOME", cacheHome);
        TimeSpan cap = TimeSpan.FromSeconds(937);

        // Act
        await Lifecycle(cap, LogEventLevel.Information).StartAsync(Ct);

        // Assert
        LogEntry started = _logs.WithProperty("ProcessId").ShouldHaveSingleItem();
        started.Level.ShouldBe(LogLevel.Information);
        started.Property("Version").ShouldBe(ServerVersion.Informational);
        started.Property("ProcessId").ShouldBe(Environment.ProcessId);
        started.Property("WorkingDirectory").ShouldBe(_environment.CurrentDirectory);
        started.Property("CacheHome").ShouldBe(cacheHome);
        started.Property("RunCap").ShouldBe("15 minutes 37 seconds");
        started.Property("PreWarm").ShouldBe("on");
        started.Property("LogLevel").ShouldBe(LogEventLevel.Information);
    }

    [Fact]
    public async Task StartAsync_PreWarmTurnedOff_SaysSoRatherThanLeavingItToBeInferred()
    {
        // Arrange — the switch's position is the reason a following call is cold, and this line is the only
        // place it appears, which is also why a disabled pass's own outcome stays at Debug.
        _environment.SetVariable(CacheWarmer.EnableVariable, "off");

        // Act
        await Lifecycle().StartAsync(Ct);

        // Assert
        _logs.WithProperty("PreWarm").ShouldHaveSingleItem().Property("PreWarm").ShouldBe("off");
    }

    [Fact]
    public async Task StopAsync_ReportsHowLongTheProcessRan()
    {
        // Arrange
        ServerLifecycle lifecycle = Lifecycle();
        await lifecycle.StartAsync(Ct);

        // Act
        await lifecycle.StopAsync(Ct);

        // Assert — the replacement for the quieted Hosting shutdown lines, and the bracket that makes a
        // session's span readable at a glance.
        LogEntry stopping = _logs.WithProperty("Uptime").ShouldHaveSingleItem();
        stopping.Level.ShouldBe(LogLevel.Information);
        stopping.Property("Uptime").ShouldNotBeNull();
    }

    [Fact]
    public async Task StartAsync_NeitherLineBelongsToARun()
    {
        // Arrange — startup and shutdown happen outside any tool call or pre-warm pass, so the RunId column
        // has nothing to carry and falls back to its fixed-width default rather than rendering empty.
        ServerLifecycle lifecycle = Lifecycle();

        // Act
        await lifecycle.StartAsync(Ct);
        await lifecycle.StopAsync(Ct);

        // Assert
        _logs.Entries.ShouldAllBe(entry => entry.ScopeValue(RunIdScope.PropertyName) == null);
    }

    private ServerLifecycle Lifecycle(TimeSpan? cap = null, LogEventLevel logLevel = LogEventLevel.Warning)
    {
        // A JbLocator that is never probed: the fingerprint reads the cache home, which needs no jb.
        JbLocator locator = new(Substitute.For<IProcessRunner>(), _environment, NullLogger<JbLocator>.Instance);
        ConfigResolver resolver = new(locator, _environment, NullLogger<ConfigResolver>.Instance);

        return new ServerLifecycle(
            resolver,
            _environment,
            cap ?? JbRunTimeout.Default,
            logLevel,
            Logs.Capturing(_logs).CreateLogger<ServerLifecycle>());
    }
}
