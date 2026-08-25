using System.Linq.Expressions;
using NSubstitute;
using NSubstitute.Core;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     The one spelling of an <see cref="IProcessRunner.RunAsync" /> argument matcher, for arranging a
///     substitute and for checking what it received.
/// </summary>
/// <remarks>
///     <para>
///         Written out by hand, the matcher was in some thirty places across fourteen files — and the failure
///         mode when the seam grew is the reason to collect it. A hand-spelled matcher listing every parameter
///         still <em>compiles</em> after a new optional one is added: it binds the newcomer to its default and
///         then silently stops matching the moment product code passes anything else. The substitute answers
///         <c>default(ProcessResult)</c>, and a dozen unrelated files fail with messages naming neither the
///         seam nor the change.
///     </para>
///     <para>
///         So the rule is that no test spells the argument list itself. The next parameter added to the seam
///         costs this file and nothing else.
///     </para>
/// </remarks>
internal static class ProcessRuns
{
    /// <summary>Any run of any program.</summary>
    public static Task<ProcessResult> AnyRun(this IProcessRunner runner)
    {
        return runner.RunAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<Action<string>?>());
    }

    /// <summary>Any run of <paramref name="fileName" />, whatever it was given.</summary>
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
    ///     Any run whose argument list satisfies <paramref name="arguments" /> — for a check that has to tell
    ///     one stubbed invocation from another.
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

    /// <summary>
    ///     The line observer the run was given, read off a recorded call. <see langword="null" /> for a caller
    ///     that asked for no progress — which is what a speculative pass and a version probe both do.
    /// </summary>
    public static Action<string>? OutputLineObserver(this CallInfo call)
    {
        return call.ArgAt<Action<string>?>(4);
    }
}