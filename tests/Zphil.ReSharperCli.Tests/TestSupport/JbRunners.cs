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
///         It is also the one place the graph's loggers are wired, which is why every overload takes an
///         optional <see cref="ILoggerFactory" />: the runner, its lock and its transplanter all write lines a
///         test may want to assert on, and handing one factory to the whole graph is what lets a single
///         <c>CapturingLoggerProvider</c> see all of them. Omitted, everything logs to a
///         <see cref="NullLogger{T}" /> — the right default for the tests that are about behaviour rather than
///         about what got recorded.
///     </para>
/// </remarks>
internal static class JbRunners
{
    /// <summary>The lock, built with a logger, for a test that has to hold or contend it itself.</summary>
    /// <remarks>Spelled here rather than at each call site so the cap and the logger stay one decision.</remarks>
    public static JbRunLock Lock(TimeSpan? cap = null, ILoggerFactory? logs = null)
    {
        return new JbRunLock(cap ?? JbRunTimeout.Default, Logs.For<JbRunLock>(logs));
    }

    /// <summary>The yield, built with a logger, for a test driving two callers against the same precedence.</summary>
    public static JbRunYield Yield(ILoggerFactory? logs = null)
    {
        return new JbRunYield(Logs.For<JbRunYield>(logs));
    }

    /// <summary>
    ///     The slot, built with a logger, for a test driving two runs at this server's one-at-a-time bound.
    /// </summary>
    public static JbRunSlot Slot(ILoggerFactory? logs = null)
    {
        return new JbRunSlot(Logs.For<JbRunSlot>(logs));
    }

    /// <summary>
    ///     A cache reset wired to the same lock and yield a runner is, as the composition root wires it.
    /// </summary>
    /// <param name="heartbeat">
    ///     Shortens the progress interval for a test that waits out more than one beat of the queue wait;
    ///     omitted, beats come at the production interval.
    /// </param>
    public static CacheResetService Reset(
        JbRunLock runLock,
        JbRunYield runYield,
        ILoggerFactory? logs = null,
        TimeSpan? heartbeat = null)
    {
        return new CacheResetService(runLock, runYield, Logs.For<CacheResetService>(logs), heartbeat);
    }

    /// <summary>
    ///     A cache reset with a lock and a yield of its own, for a test about the reset alone.
    /// </summary>
    /// <remarks>
    ///     It arbitrates against nothing: no runner shares its yield, so it can never stand a pre-warm down, and
    ///     only a holder of the lock <em>file</em> can make it queue — which is what such a test wants, and why
    ///     this is not <see cref="Reset" />.
    /// </remarks>
    /// <param name="cap">Bounds its queue wait.</param>
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

    /// <summary>For tests that hold or contend the lock themselves, so the lock has to be theirs.</summary>
    public static JbRunner Create(
        IProcessRunner processRunner,
        JbRunLock runLock,
        TimeSpan? cap = null,
        ILoggerFactory? logs = null)
    {
        return Create(processRunner, runLock, Yield(logs), cap, logs);
    }

    /// <summary>
    ///     For tests that drive a second caller — a cache reset — against the same precedence, so the yield
    ///     has to be theirs too.
    /// </summary>
    /// <remarks>
    ///     The server-wide <c>jb</c> bound is the runner's own: nothing but the runner takes it, so no test has
    ///     a second holder to share it with.
    /// </remarks>
    /// <param name="heartbeat">
    ///     Shortens the progress interval for a test that waits out more than one beat; omitted, beats come at
    ///     the production interval.
    /// </param>
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