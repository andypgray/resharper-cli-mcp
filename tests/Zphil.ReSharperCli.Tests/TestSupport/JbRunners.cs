using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Assembles the <see cref="JbRunner" /> graph the way the composition root does: one
///     <see cref="JbRunLock" /> shared by the runner and its <see cref="CacheTransplanter" />, and one cap
///     wired to both the lock's queue wait and the run timeout. A transplanter with a lock of its own would
///     serialize against nothing and could touch a generation mid-run, so that sharing is an invariant to
///     assemble in one place rather than re-establish in every test constructor.
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
        return new JbRunner(processRunner, runLock, new CacheTransplanter(runLock), cap ?? JbRunTimeout.Default);
    }
}