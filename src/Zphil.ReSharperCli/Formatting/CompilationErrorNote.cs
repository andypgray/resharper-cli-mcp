using Zphil.ReSharperCli.Resources;
using Zphil.ReSharperCli.Sarif;
using Zphil.ReSharperCli.Tools;

namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     The note an inspect result leads with when it reports compilation errors. Two quite different things
///     produce them, and the cheap one is much the commoner: a checkout that has not been built — or whose
///     packages have never been restored, where every unresolved reference reports here at once. The other is
///     a stale ReSharper index, which can serve errors for symbols the compiler resolves perfectly well and
///     stays wrong across re-runs until the cache generation is dropped. A whole session has been spent
///     deriving the second from first principles; this is that derivation reduced to a few lines, delivered
///     at the moment it is needed rather than in a guide nobody has a reason to open.
/// </summary>
/// <remarks>
///     <para>
///         It states the discriminator rather than the conclusion, and this is the rule the text is held to
///         rather than a description of it: for a while the two had drifted apart. This server cannot tell a
///         phantom from a genuine compilation error — both arrive as <see cref="RuleId" /> — and an agent
///         halfway through an edit usually has the genuine kind. So the note leads with the test that
///         separates them (build it; see whether the compiler agrees) and makes the cure conditional on the
///         answer. Telling an agent with a real syntax error to drop its cache would be worse than saying
///         nothing.
///     </para>
///     <para>
///         Which is why the reset is gated rather than imperative, and why its cost is named. On a checkout
///         that was never built — a fresh worktree is the measured case, 13,990 compilation errors out of
///         17,971 findings — a reset is the expensive wrong move: it drops the generation, writes a cold
///         tombstone that blocks seeding from a sibling checkout, and buys a cold rebuild on top of the
///         build that was needed anyway. Nothing here detects which branch applies. There is no honest
///         signal: <c>bin</c> and <c>obj</c> on disk are not "built", and a proportion heuristic misreads a
///         mid-refactor session whose broken base type cascades exactly the same way. This note is charged
///         to every affected response and rides outside the ladder, so a wrong branch would be a wrong
///         instruction at maximum prominence — naming both and choosing neither is the honest shape.
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