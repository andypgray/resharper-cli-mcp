namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     Renders the note an inspect result leads with when an entry in its <c>files</c> scope named no file on
///     disk.
/// </summary>
/// <remarks>
///     <para>
///         Whatever the cause, a typo or a path gone stale, the scan runs over the rest, and without this note
///         nothing in the response says so.
///     </para>
///     <para>
///         Reported rather than thrown, unlike cleanup's equivalent: inspect mutates nothing, and since a
///         <c>files</c> scope buys no time, failing the call would charge a second full run for what a note
///         gives free.
///     </para>
///     <para>
///         <c>inspectcode</c> through 2026.1 exits 0 reporting nothing when every entry misses, so there this
///         note is the only sign; 2026.2 exits 3, which fails the call by itself. No release reports the
///         partial case: some entries match, <c>jb</c> exits 0 with their findings, and says nothing about the
///         rest.
///     </para>
///     <para>
///         It vouches for nothing. <c>jb</c> matches <c>--include</c> against the solution model, so a path
///         that resolves on disk can match nothing there, and SARIF's <c>run.artifacts</c> lists only the files
///         that drew a finding (<c>jb</c> 2026.2.3.1; the <c>JbContract</c> suite watches it), so an entry that
///         matched nothing reads as a clean file. The note's tail states that limit for the entries it lists
///         rather than as a caveat on every clean scoped run.
///     </para>
/// </remarks>
internal static class InspectScopeNote
{
    /// <summary>
    ///     How many unresolved entries are listed before the tail collapses to a count, through
    ///     <see cref="IssueMarkdownFormatter.Collapse" />.
    /// </summary>
    /// <remarks>
    ///     Diverges deliberately from <c>CleanupService</c>'s unbounded missing-file list: there the call failed
    ///     and the message is the whole response, while this is a prefix competing with findings for the same
    ///     budget.
    /// </remarks>
    private const int MaxListedEntries = 10;

    /// <summary>
    ///     Renders the note for <paramref name="missing" />, the entries of a
    ///     <paramref name="scopeCount" />-entry scope that resolved to no file, or <c>""</c> when there are none.
    /// </summary>
    /// <remarks>
    ///     The caller classifies the entries before the run, so this reads nothing from disk. Wildcards are not
    ///     among them: <c>jb</c> expands those, and this server cannot say what they matched.
    /// </remarks>
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