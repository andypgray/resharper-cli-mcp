using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Assembles the <see cref="JbRunner" /> graph the way the composition root does: one
///     <see cref="JbRunLock" /> shared by the runner and its <see cref="CacheTransplanter" />, one
///     <see cref="JbRunYield" /> shared by the runner and every other caller the user waits on, and one cap
///     wired to both the lock's queue wait and the run timeout. A transplanter with a lock of its own would
///     serialize against nothing and could touch a generation mid-run; a <see cref="CacheResetService" />
///     with a yield of its own would compile, pass, and arbitrate against nothing at all. Both are
///     invariants to assemble in one place rather than re-establish in every test constructor.
/// </summary>
internal static class JbRunners
{
    public static JbRunner Create(IProcessRunner processRunner, TimeSpan? cap = null)
    {
        return Create(processRunner, new JbRunLock(cap ?? JbRunTimeout.Default), cap);
    }

    /// <summary>For tests that hold or contend the lock themselves, so the lock has to be theirs.</summary>
    public static JbRunner Create(IProcessRunner processRunner, JbRunLock runLock, TimeSpan? cap = null)
    {
        return Create(processRunner, runLock, new JbRunYield(), cap);
    }

    /// <summary>
    ///     For tests that drive a second caller — a cache reset — against the same precedence, so the yield
    ///     has to be theirs too.
    /// </summary>
    public static JbRunner Create(
        IProcessRunner processRunner,
        JbRunLock runLock,
        JbRunYield runYield,
        TimeSpan? cap = null)
    {
        return new JbRunner(processRunner, runLock, runYield, new CacheTransplanter(runLock), cap ?? JbRunTimeout.Default);
    }
}