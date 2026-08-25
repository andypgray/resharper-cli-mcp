using Zphil.ReSharperCli.Resources;
using Zphil.ReSharperCli.Sarif;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     The note an inspect result leads with when it reports compilation errors: ReSharper's solution-wide
///     index can serve errors for symbols the compiler resolves perfectly well, and once it does it stays
///     wrong across re-runs until the cache generation is dropped. A whole session has been spent deriving
///     that from first principles; this is that derivation reduced to three lines, delivered at the moment
///     it is needed rather than in a guide nobody has a reason to open.
/// </summary>
/// <remarks>
///     <para>
///         It states the discriminator rather than the conclusion. This server cannot tell a phantom from a
///         genuine compilation error — both arrive as <see cref="RuleId" /> — and an agent halfway through an
///         edit usually has the genuine kind. So the note leads with the test that separates them (build it;
///         see whether the compiler agrees) and makes the cure conditional on the answer. Telling an agent
///         with a real syntax error to drop its cache would be worse than saying nothing.
///     </para>
///     <para>
///         Joined onto <see cref="ConfigWarningBanner" />'s output rather than folded into it: that banner's
///         subject is configuration silently dropped before the run, and this is a reading of the run's
///         results. They share only the position, and the property that
///         <c>ResponseTruncator.BudgetForBody</c> charges them to the budget before rendering, which puts
///         both outside the reduction ladder — a note that vanished at <c>Minimal</c> would disappear
///         precisely when a wall of phantom errors made the response too big.
///     </para>
/// </remarks>
internal static class CompilationErrorNote
{
    /// <summary>
    ///     The rule <c>jb</c> reports every C# compilation error under, phantom or genuine. The leading dot is
    ///     jb's own and is verified rather than assumed: <c>jb</c> writes <c>.CSharpErrors</c> in both the
    ///     SARIF results and the driver's rule table, and <c>SarifParser</c> passes the id through untouched.
    ///     It is also what an agent reads in the rendered result, so the note quotes the same spelling. The
    ///     <c>JbContract</c> suite re-reads that against each release rather than a version being pinned here,
    ///     and reports a rename instead of failing, because <see cref="UndottedRuleId" /> already absorbs the
    ///     likeliest one.
    /// </summary>
    internal const string RuleId = ".CSharpErrors";

    /// <summary>
    ///     The same rule without jb's leading dot. Matched as well as <see cref="RuleId" /> because nothing
    ///     documents that dot: it costs one comparison to keep the note working if a jb release drops it.
    /// </summary>
    private const string UndottedRuleId = "CSharpErrors";

    /// <summary>
    ///     The note for <paramref name="issues" />, or <c>""</c> when none of them is a compilation error. The
    ///     resolved <paramref name="cacheHome" /> is named outright, because the caller cannot derive it — it
    ///     comes from <c>JB_CACHE_HOME</c> or a default this server picked.
    /// </summary>
    public static string For(IReadOnlyList<InspectIssue> issues, string cacheHome)
    {
        int errors = issues.Count(issue => IsCompilationError(issue.RuleId));
        if (errors == 0) return "";

        return $"NOTE: {errors} of these issue(s) are compilation errors (`{RuleId}`). Build the solution before "
               + "acting on them: if the compiler accepts the code, ReSharper's solution-wide index is stale and "
               + $"these are phantoms that will repeat on every re-run. Run {ResharperTools.ResetCacheToolName} to drop this "
               + $"solution's cache generation under \"{cacheHome}\", then inspect again. "
               + $"See the {ResharperResources.SetupGuideUri} resource.\n\n";
    }

    private static bool IsCompilationError(string ruleId)
    {
        return string.Equals(ruleId, RuleId, StringComparison.Ordinal)
               || string.Equals(ruleId, UndottedRuleId, StringComparison.Ordinal);
    }
}