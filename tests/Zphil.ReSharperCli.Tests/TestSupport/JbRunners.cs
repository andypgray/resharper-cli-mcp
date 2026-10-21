using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Assembles the <see cref="JbRunner" /> graph the way the composition root does.
/// </summary>
/// <remarks>
///     <para>
///         One <see cref="JbRunLock" /> is shared by the runner and its <see cref="CacheTransplanter" />, one
///         <see cref="JbRunYield" /> by the runner and every other caller the user waits on, and one cap is
///         wired to both the lock's queue wait and the run timeout. A transplanter with a lock of its own would
///         serialize against nothing and could touch a generation mid-run; a <see cref="CacheResetService" />
///         with a yield of its own would compile, pass, and arbitrate against nothing at all. Both are
///         invariants to assemble in one place rather than re-establish in every test constructor.
///     </para>
///     <para>
///         It is also the one place the graph's loggers are wired, which is why every helper here takes an
///         optional <see cref="ILoggerFactory" />: handing one factory to the whole graph is what lets a single
///         <c>CapturingLoggerProvider</c> see every line the graph writes. Omitted, everything logs to a
///         <see cref="NullLogger{T}" />, the right default for a test about behaviour rather than about what got
///         recorded. A helper taking a <c>heartbeat</c> shortens the progress interval to it, for a test that
///         waits out more than one beat; omitted, beats come at the production interval.
///     </para>
/// </remarks>
internal static class JbRunners
{
    /// <summary>Builds the lock with a logger, for a test that has to hold or contend it itself.</summary>
    /// <remarks>Spelled here rather than at each call site so the cap and the logger stay one decision.</remarks>
    public static JbRunLock Lock(TimeSpan? cap = null, ILoggerFactory? logs = null)
    {
        return new JbRunLock(cap ?? JbRunTimeout.Default, Logs.For<JbRunLock>(logs));
    }

    /// <summary>Builds the yield with a logger, for a test driving two callers against one precedence.</summary>
    public static JbRunYield Yield(ILoggerFactory? logs = null)
    {
        return new JbRunYield(Logs.For<JbRunYield>(logs));
    }

    /// <summary>
    ///     Builds the slot with a logger, for a test driving two runs against this server's one-at-a-time bound.
    /// </summary>
    public static JbRunSlot Slot(ILoggerFactory? logs = null)
    {
        return new JbRunSlot(Logs.For<JbRunSlot>(logs));
    }

    /// <summary>
    ///     Builds a cache reset on the caller's lock and yield, sharing them with a runner as the composition
    ///     root does.
    /// </summary>
    public static CacheResetService Reset(
        JbRunLock runLock,
        JbRunYield runYield,
        ILoggerFactory? logs = null,
        TimeSpan? heartbeat = null)
    {
        return new CacheResetService(runLock, runYield, Logs.For<CacheResetService>(logs), heartbeat);
    }

    /// <summary>
    ///     Builds a cache reset with a lock and a yield of its own, for a test about the reset alone.
    /// </summary>
    /// <remarks>
    ///     It arbitrates against nothing: no runner shares its yield, so it can never stand a pre-warm down, and
    ///     only a holder of the lock file can make it queue, which is what such a test wants and why this is not
    ///     <see cref="Reset" />. <paramref name="cap" /> bounds its queue wait.
    /// </remarks>
    public static CacheResetService StandaloneReset(
        TimeSpan cap,
        ILoggerFactory? logs = null,
        TimeSpan? heartbeat = null)
    {
        return Reset(Lock(cap, logs), Yield(logs), logs, heartbeat);
    }

    public static JbRunner Create(IProcessRunner processRunner, TimeSpan? cap = null, ILoggerFactory? logs = null)
    {
        return Create(processRunner, Lock(cap, logs), cap, logs);
    }

    /// <summary>
    ///     Builds a runner on the caller's <paramref name="runLock" />, for a test holding or contending it.
    /// </summary>
    public static JbRunner Create(
        IProcessRunner processRunner,
        JbRunLock runLock,
        TimeSpan? cap = null,
        ILoggerFactory? logs = null)
    {
        return Create(processRunner, runLock, Yield(logs), cap, logs);
    }

    /// <summary>
    ///     Builds a runner on the caller's lock and yield, for a test that drives a second caller, such as a
    ///     cache reset, against the same precedence.
    /// </summary>
    /// <remarks>
    ///     Builds its own run slot. Anything else that takes the slot would have to share this one, as the lock
    ///     and the yield are shared.
    /// </remarks>
    public static JbRunner Create(
        IProcessRunner processRunner,
        JbRunLock runLock,
        JbRunYield runYield,
        TimeSpan? cap = null,
        ILoggerFactory? logs = null,
        TimeSpan? heartbeat = null)
    {
        return new JbRunner(
            processRunner,
            runLock,
            runYield,
            Slot(logs),
            new CacheTransplanter(runLock, Logs.For<CacheTransplanter>(logs)),
            cap ?? JbRunTimeout.Default,
            Logs.For<JbRunner>(logs),
            heartbeat);
    }
}