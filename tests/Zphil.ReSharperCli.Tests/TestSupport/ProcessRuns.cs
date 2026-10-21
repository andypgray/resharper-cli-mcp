using System.Linq.Expressions;
using NSubstitute;
using NSubstitute.Core;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Provides the one spelling of an <see cref="IProcessRunner.RunAsync" /> argument matcher, for arranging a
///     substitute and for checking what it received.
/// </summary>
/// <remarks>
///     A hand-spelled matcher listing every parameter still compiles after a new optional one is added to the
///     seam: it binds the newcomer to its default and then silently stops matching the moment product code
///     passes anything else. The substitute answers <c>default(ProcessResult)</c>, and unrelated files fail
///     with messages naming neither the seam nor the change. So no test spells the argument list itself, and
///     the next parameter added to the seam costs this file and nothing else.
/// </remarks>
internal static class ProcessRuns
{
    /// <summary>Matches any run of any program.</summary>
    public static Task<ProcessResult> AnyRun(this IProcessRunner runner)
    {
        return runner.RunAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<Action<string>?>());
    }

    /// <summary>Matches any run of <paramref name="fileName" />, whatever it was given.</summary>
    public static Task<ProcessResult> AnyRunOf(this IProcessRunner runner, string fileName)
    {
        return runner.RunAsync(
            fileName,
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<Action<string>?>());
    }

    /// <summary>
    ///     Matches any run whose argument list satisfies <paramref name="arguments" />, for a check that has to
    ///     tell one stubbed invocation from another.
    /// </summary>
    public static Task<ProcessResult> AnyRunWith(
        this IProcessRunner runner,
        Expression<Predicate<IReadOnlyList<string>>> arguments)
    {
        return runner.RunAsync(
            Arg.Any<string>(),
            Arg.Is(arguments),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<Action<string>?>());
    }

    /// <summary>Returns the argument list the run was given, read off a recorded call.</summary>
    public static IReadOnlyList<string> Arguments(this CallInfo call)
    {
        return call.ArgAt<IReadOnlyList<string>>(1);
    }

    /// <summary>Returns the cancellation token the run was given, read off a recorded call.</summary>
    public static CancellationToken Token(this CallInfo call)
    {
        return call.ArgAt<CancellationToken>(3);
    }

    /// <summary>Returns the line observer the run was given, read off a recorded call.</summary>
    /// <remarks><see langword="null" /> for a caller that asked for no progress.</remarks>
    public static Action<string>? OutputLineObserver(this CallInfo call)
    {
        return call.ArgAt<Action<string>?>(4);
    }
}