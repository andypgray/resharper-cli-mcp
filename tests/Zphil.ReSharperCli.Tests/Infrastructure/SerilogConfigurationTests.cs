using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Infrastructure;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Zphil.ReSharperCli.Tests.Infrastructure;

/// <summary>
///     <see cref="SerilogConfiguration" />: the level policy of the file log, driven through the real
///     <see cref="SerilogConfiguration.Configure" /> against an in-memory sink, and
///     <see cref="SerilogConfiguration.ParseLogLevel" />.
/// </summary>
/// <remarks>
///     <para>
///         Two claims are pinned here that nothing else can pin, and both are the kind that fails silently.
///         The first is that the <see cref="SerilogConfiguration.QuietedCategories" /> really are quieted: a
///         filter that does not take effect looks exactly like a log that was always this noisy.
///     </para>
///     <para>
///         The second is the enricher order behind the <c>{RunId}</c> column, which
///         <see cref="SerilogConfiguration.Configure" /> explains: getting it backwards would give every line in
///         the file the same <c>----</c>, silently, with the column still present and still looking right.
///     </para>
///     <para>
///         Driven through <see cref="SerilogLoggerFactory" /> rather than by pushing onto Serilog's
///         <c>LogContext</c> directly, because that is the path production takes: the scope is opened on an
///         <c>ILogger</c>, and it reaches the output template through the Serilog provider's own enricher.
///         Pushing by hand would test an arrangement no code here uses.
///     </para>
/// </remarks>
public sealed class SerilogConfigurationTests
{
    [Theory]
    // Microsoft.Extensions.Logging names map through LevelConvert.
    [InlineData("Information", LogEventLevel.Information)]
    [InlineData("Trace", LogEventLevel.Verbose)]
    [InlineData("Critical", LogEventLevel.Fatal)]
    // Serilog names are accepted directly.
    [InlineData("Verbose", LogEventLevel.Verbose)]
    [InlineData("Fatal", LogEventLevel.Fatal)]
    // Case-insensitive.
    [InlineData("eRRoR", LogEventLevel.Error)]
    // Unrecognised input falls back to Warning.
    [InlineData("99", LogEventLevel.Warning)]
    [InlineData("nonsense", LogEventLevel.Warning)]
    [InlineData("", LogEventLevel.Warning)]
    [InlineData("   ", LogEventLevel.Warning)]
    [InlineData(null, LogEventLevel.Warning)]
    public void ParseLogLevel_MapsKnownNamesAndFallsBackToWarning(string? envValue, LogEventLevel expected)
    {
        // Act
        LogEventLevel level = SerilogConfiguration.ParseLogLevel(envValue);

        // Assert
        level.ShouldBe(expected);
    }

    /// <summary>A category from each quieted framework, spelled as the SDK and Hosting actually emit them.</summary>
    [Theory]
    [InlineData("ModelContextProtocol.Server.McpServer")]
    [InlineData("Microsoft.Hosting.Lifetime")]
    public void FrameworkCategory_BelowWarning_IsDropped(string category)
    {
        // Arrange
        CollectingSink sink = new();
        using SerilogLoggerFactory factory = Factory(sink, LogEventLevel.Debug);
        ILogger logger = factory.CreateLogger(category);

        // Act
        logger.LogInformation("chatter");
        logger.LogDebug("more chatter");
        logger.LogWarning("something actually wrong");

        // Assert — the warning survives, because a framework failure is still a failure.
        sink.Events.Select(entry => entry.Level).ShouldBe([LogEventLevel.Warning]);
    }

    [Fact]
    public void ServerCategory_AtInformation_Survives()
    {
        // Arrange — the same run that drops the chatter above has to keep this, or the policy is just silence.
        CollectingSink sink = new();
        using SerilogLoggerFactory factory = Factory(sink, LogEventLevel.Information);
        ILogger logger = factory.CreateLogger("Zphil.ReSharperCli.Services.JbRunner");

        // Act
        logger.LogInformation("jb inspectcode starting");

        // Assert
        sink.Events.ShouldHaveSingleItem().Level.ShouldBe(LogEventLevel.Information);
    }

    [Fact]
    public void FrameworkOverride_NeverTalksBelowTheLevelAskedFor()
    {
        // Arrange — an override is there to quiet a category. Pinned at Error because that is where a fixed
        // Warning override would invert the policy and leave the frameworks the *only* thing still talking.
        CollectingSink sink = new();
        using SerilogLoggerFactory factory = Factory(sink, LogEventLevel.Error);
        ILogger logger = factory.CreateLogger("ModelContextProtocol.Server.McpServer");

        // Act
        logger.LogWarning("a warning nobody asked to see");

        // Assert
        sink.Events.ShouldBeEmpty();
    }

    [Fact]
    public void RunIdColumn_OutsideAnyRun_RendersTheFixedWidthDefault()
    {
        // Arrange
        CollectingSink sink = new();
        using SerilogLoggerFactory factory = Factory(sink, LogEventLevel.Information);
        ILogger logger = factory.CreateLogger("Zphil.ReSharperCli.Infrastructure.ServerLifecycle");

        // Act — a startup line belongs to no run.
        logger.LogInformation("started");

        // Assert
        RunIdOf(sink.Events.ShouldHaveSingleItem()).ShouldBe(RunIdScope.OutsideARun);
    }

    [Fact]
    public void RunIdColumn_InsideARunScope_CarriesTheRunIdRatherThanTheDefault()
    {
        // Arrange
        CollectingSink sink = new();
        using SerilogLoggerFactory factory = Factory(sink, LogEventLevel.Information);
        ILogger opener = factory.CreateLogger("Zphil.ReSharperCli.Pipeline.GlobalCallToolFilter");

        // A second category, because the whole point of a scope over a logger is that the lines it tags are
        // written by other classes further down the call.
        ILogger downstream = factory.CreateLogger("Zphil.ReSharperCli.Services.JbRunner");

        // Act
        string inside;
        using (RunIdScope.Begin(opener))
        {
            downstream.LogInformation("jb inspectcode starting");
            inside = RunIdOf(sink.Events.ShouldHaveSingleItem());
        }

        downstream.LogInformation("after the scope closed");

        // Assert — the scope wins inside, and the default is back once it closes.
        inside.ShouldNotBe(RunIdScope.OutsideARun);
        inside.Length.ShouldBe(RunIdScope.OutsideARun.Length);
        RunIdOf(sink.Events.Last()).ShouldBe(RunIdScope.OutsideARun);
    }

    [Fact]
    public void QuietedCategories_AreTheTwoFrameworksThisServerRidesOn()
    {
        // Assert — a third framework added to the graph must be a deliberate decision here rather than a
        // surprise in the log, and dropping one of these silently re-floods it.
        SerilogConfiguration.QuietedCategories.ShouldBe(["ModelContextProtocol", "Microsoft.Hosting"]);
    }

    /// <summary>The production logger arrangement — <see cref="SerilogConfiguration.Configure" /> plus a sink.</summary>
    private static SerilogLoggerFactory Factory(CollectingSink sink, LogEventLevel minimumLevel)
    {
        Logger logger = SerilogConfiguration
            .Configure(new LoggerConfiguration(), minimumLevel)
            .WriteTo.Sink(sink)
            .CreateLogger();

        return new SerilogLoggerFactory(logger, true);
    }

    private static string RunIdOf(LogEvent entry)
    {
        return entry.Properties[RunIdScope.PropertyName].ToString().Trim('"');
    }

    /// <summary>Keeps every event that reached the sink, so a test can assert on what the policy let through.</summary>
    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToList();
                }
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
            {
                _events.Add(logEvent);
            }
        }
    }
}