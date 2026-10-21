using Zphil.ReSharperCli.Resources;
using Zphil.ReSharperCli.Sarif;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     Renders the note an inspect result leads with when it reports compilation errors.
/// </summary>
/// <remarks>
///     <para>
///         Two causes produce them, and the cheap one is the commoner: a checkout that has not been built, or
///         whose packages were never restored, where every unresolved reference reports here at once. The other
///         is a stale ReSharper index, which serves errors for symbols the compiler resolves and stays wrong
///         across re-runs until the cache generation is dropped.
///     </para>
///     <para>
///         The note states the test that separates the two (build, and see whether the compiler agrees) and
///         makes the cure conditional on its answer, because this server cannot tell a phantom from a genuine
///         error: both arrive as <see cref="RuleId" />, and an agent halfway through an edit usually has the
///         genuine kind. Telling it to drop its cache would be worse than saying nothing.
///     </para>
///     <para>
///         The reset is gated and its cost named because on a checkout that was never built it is the expensive
///         wrong move: it drops the generation, writes a cold tombstone that blocks seeding from a sibling
///         checkout, and buys a cold rebuild on top of the build that was needed anyway. Nothing here detects
///         which case applies, since no signal this server can read tells them apart, so the note names both
///         and chooses neither.
///     </para>
///     <para>
///         It sits outside the reduction ladder, so it cannot vanish at <c>Minimal</c> when a wall of phantom
///         errors is what made the response too big.
///     </para>
/// </remarks>
internal static class CompilationErrorNote
{
    /// <summary>
    ///     The rule <c>jb</c> reports every C# compilation error under, phantom or genuine.
    /// </summary>
    /// <remarks>
    ///     The leading dot is jb's own: <c>jb</c> writes <c>.CSharpErrors</c> in both the SARIF results and the
    ///     driver's rule table, and <c>SarifParser</c> passes the id through untouched, so the note quotes the
    ///     spelling an agent reads in the rendered result. The <c>JbContract</c> suite re-reads it on each
    ///     release and reports a rename rather than failing, because <see cref="UndottedRuleId" /> absorbs the
    ///     likeliest one.
    /// </remarks>
    internal const string RuleId = ".CSharpErrors";

    /// <summary>
    ///     The same rule without jb's leading dot. Matched as well as <see cref="RuleId" /> because nothing
    ///     documents that dot: it costs one comparison to keep the note working if a jb release drops it.
    /// </summary>
    private const string UndottedRuleId = "CSharpErrors";

    /// <summary>
    ///     Renders the note for <paramref name="issues" />, or <c>""</c> when none of them is a compilation
    ///     error.
    /// </summary>
    /// <remarks>
    ///     The resolved <paramref name="cacheHome" /> is named outright, because the caller cannot derive it —
    ///     it comes from <c>JB_CACHE_HOME</c> or a default this server picked.
    /// </remarks>
    public static string For(IReadOnlyList<InspectIssue> issues, string cacheHome)
    {
        int errors = issues.Count(issue => IsCompilationError(issue.RuleId));
        if (errors == 0) return "";

        return $"NOTE: {errors} of these issue(s) are compilation errors (`{RuleId}`). Build the solution "
               + "first: on a checkout that was never built or never restored, every unrestored package "
               + "reference reports here. If the build succeeds and an inspect still reports them, they are "
               + $"phantoms from a stale ReSharper index; run {ResharperTools.ResetCacheToolName} to drop this "
               + $"solution's cache generation under \"{cacheHome}\", which costs the next call a cold "
               + $"analysis. See the {ResharperResources.SetupGuideUri} resource.\n\n";
    }

    private static bool IsCompilationError(string ruleId)
    {
        return string.Equals(ruleId, RuleId, StringComparison.Ordinal)
               || string.Equals(ruleId, UndottedRuleId, StringComparison.Ordinal);
    }
}