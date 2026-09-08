namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     The note an inspect result leads with when an entry in its <c>files</c> scope named no file on disk.
///     A typo, a stale path, a file deleted since the plan that listed it — the scan then ran over the rest,
///     and nothing in the response said so.
/// </summary>
/// <remarks>
///     <para>
///         Reported rather than thrown, unlike cleanup's equivalent. Inspect is read-only, so nothing was
///         mutated and there is nothing to undo; and a <c>files</c> scope is measured to buy no time at all
///         (269 s scoped against 272 s solution-wide), so failing the call would charge a full second run
///         for information a note gives away free.
///     </para>
///     <para>
///         Its value is version-independence and the partial case, not typo-catching. <c>jb</c>'s own
///         behaviour when <em>every</em> pattern misses has already changed once — <c>inspectcode</c> exited
///         0 reporting nothing through 2026.1 and exits 3 in 2026.2 — so on a current <c>jb</c> the all-miss
///         case fails loudly by itself. What no release reports is the <em>partial</em> one: some entries
///         match, <c>jb</c> exits 0 with their findings, and says nothing about the rest.
///     </para>
///     <para>
///         It vouches for nothing. There is no "the other paths ran as asked" here, because this server
///         cannot establish it: <c>jb</c> matches <c>--include</c> against the solution model, and a file
///         that is on disk but in no project matches nothing while resolving perfectly well here. That limit
///         is stated in the note's own tail, where it is grounded on entries the caller already has cause to
///         act on, rather than as a caveat on every clean scoped run — which would fire at a near-total
///         false-positive rate on the commonest scoped call there is.
///     </para>
///     <para>
///         Second of inspect's preambles, after <see cref="ConfigWarningBanner" /> and before
///         <see cref="CompilationErrorNote" />, so the block reads before-the-run, then the run's scope, then
///         how to read the results, then where the file went. A dropped <c>files</c> entry is the same class
///         of statement as a dropped settings file; it is a separate class from that banner because
///         <see cref="ConfigWarningBanner" />'s entire input is <see cref="Discovery.ConfigWarnings" />, which
///         a <c>files</c> entry is not. Like its neighbours it is charged to the budget by
///         <c>ResponseTruncator.BudgetForBody</c> before rendering, which puts it outside the reduction
///         ladder.
///     </para>
/// </remarks>
internal static class InspectScopeNote
{
    /// <summary>
    ///     How many unresolved entries are listed before the tail collapses to a count, through
    ///     <see cref="IssueMarkdownFormatter.Collapse" />. Diverges deliberately from
    ///     <c>CleanupService</c>'s unbounded missing-file list: there the call failed and the message is the
    ///     whole response, while this is a prefix competing with findings for the same budget.
    /// </summary>
    private const int MaxListedEntries = 10;

    /// <summary>
    ///     The note for <paramref name="missing" />, the entries of a <paramref name="scopeCount" />-entry
    ///     scope that resolved to no file, or <c>""</c> when there are none. The classification is the
    ///     caller's, through <c>FilePathList.FindMissing</c> — the one rule both tools ask, applied before
    ///     the run as cleanup applies it — so this class reads nothing from disk, as the rest of
    ///     <c>Formatting/</c> does not. Wildcards never reach it: <c>jb</c> expands those, and this server
    ///     cannot say what they matched.
    /// </summary>
    public static string For(IReadOnlyList<string> missing, int scopeCount, string solutionDirectory)
    {
        if (missing.Count == 0) return "";

        List<string> quoted = missing.Select(entry => $"\"{entry}\"").ToList();
        string listed = IssueMarkdownFormatter.Collapse(quoted, MaxListedEntries);

        return $"NOTE: {missing.Count} of the {scopeCount} files entry(s) named no file under the solution "
               + $"root \"{solutionDirectory}\": {listed}. Those were not inspected. A path that does exist "
               + "can still match nothing, because jb matches the files that belong to a project in the "
               + "solution.\n\n";
    }
}