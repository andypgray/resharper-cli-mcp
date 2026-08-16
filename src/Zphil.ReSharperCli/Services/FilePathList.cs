using System.Diagnostics.CodeAnalysis;

namespace Zphil.ReSharperCli.Services;

/// <summary>
///     Normalizes the <c>files</c> argument both tools take. A caller that joins several paths into one
///     array element — <c>["a.cs, b.cs"]</c> rather than <c>["a.cs", "b.cs"]</c> — is a measured, recurring
///     mistake that the array parameter itself invites, and it fails in two different ways: cleanup rejects
///     the joined string as a missing file, while inspect hands it to <c>jb</c>, matches nothing, and reports
///     "No issues found." — a false negative, which is worse. Splitting the element at the tool edge makes
///     both work.
/// </summary>
/// <remarks>
///     Two normalizations, applied at different depths on purpose. Splitting happens at the tool edge, before
///     validation, so the error names the fragment that is wrong. Translating an entry into the spelling
///     <c>jb</c> matches (<see cref="ToIncludePattern" />) happens at the <c>--include</c> boundary, so the
///     caller's own path is what cleanup echoes back in its report.
/// </remarks>
internal static class FilePathList
{
    private static readonly char[] Delimiters = [';', ','];

    /// <summary>
    ///     Split any entry that joins several paths into separate entries, resolving relative fragments
    ///     against <paramref name="solutionDirectory" /> only to decide whether an entry is already a real
    ///     file. Returns <paramref name="files" /> itself when nothing needed splitting.
    /// </summary>
    /// <remarks>
    ///     An entry that names an existing file is kept verbatim, so a legitimate <c>Foo,Bar.cs</c> on disk is
    ///     never reinterpreted. That guard is what makes this safe for the destructive tool: a split can only
    ///     ever turn a call that was guaranteed to fail into one that works. An entry that is nothing but
    ///     delimiters is also kept verbatim, so the existing validation reports what the caller actually sent.
    /// </remarks>
    [return: NotNullIfNotNull(nameof(files))]
    public static IReadOnlyList<string>? Split(IReadOnlyList<string>? files, string solutionDirectory)
    {
        if (files is not { Count: > 0 }) return files;

        List<string>? split = null;
        for (var i = 0; i < files.Count; i++)
        {
            string entry = files[i];
            List<string>? fragments = SplitEntry(entry, solutionDirectory);

            if (fragments is null)
            {
                split?.Add(entry);
                continue;
            }

            split ??= [.. files.Take(i)];
            split.AddRange(fragments);
        }

        return split ?? files;
    }

    /// <summary>
    ///     The absolute path <paramref name="entry" /> resolves to against
    ///     <paramref name="solutionDirectory" /> (an absolute entry ignores it). The one spelling of the
    ///     resolution rule — validation and cleanup's before/after hashing all resolve through here, so a
    ///     change to how an entry maps to a file cannot leave them pointing at different paths.
    /// </summary>
    public static string Resolve(string entry, string solutionDirectory)
    {
        return Path.GetFullPath(entry, solutionDirectory);
    }

    /// <summary>
    ///     The spelling of <paramref name="entry" /> that <c>jb</c>'s <c>--include</c> can match: relative to
    ///     <paramref name="solutionDirectory" />, forward-slashed. The one spelling of the <em>jb-pattern</em>
    ///     rule, as <see cref="Resolve" /> is the one spelling of the <em>filesystem</em> rule.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>--include</c> takes "a set of relative paths" and wildcards, per <c>jb</c>'s own help text,
    ///         and it matches them against the solution model rather than against the disk. An absolute entry
    ///         therefore becomes an Ant pattern that matches nothing: <c>cleanupcode</c> exits 3 with "No items
    ///         were found to cleanup", and <c>inspectcode</c> exits 0 and reports no issues at all — a silent
    ///         false negative. Both tools have always documented an absolute path as accepted, so this
    ///         translation is what makes that true rather than a new restriction.
    ///     </para>
    ///     <para>
    ///         Fully qualified, not merely rooted: on Windows a drive-relative <c>/src/Foo.cs</c> is already
    ///         the relative form <c>jb</c> wants, and relativising it would turn it into <c>../src/Foo.cs</c>.
    ///         An entry outside the solution directory becomes a <c>../</c> form, which is still the relative
    ///         path <c>jb</c> asked for — projects above the solution file are a legitimate layout — and an
    ///         entry on another volume has no relative form, so <see cref="Path.GetRelativePath" /> returns it
    ///         unchanged, which is the correct verbatim fallback.
    ///     </para>
    /// </remarks>
    public static string ToIncludePattern(string entry, string solutionDirectory)
    {
        try
        {
            if (!Path.IsPathFullyQualified(entry)) return entry;

            string relative = Path.GetRelativePath(solutionDirectory, entry);
            return relative.Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            // A path the runtime rejects outright (an embedded null, say) is left for the validation that
            // reports it, exactly as ResolvesToExistingFile leaves it.
            return entry;
        }
    }

    /// <summary>
    ///     Whether <paramref name="entry" /> names a file that exists, per <see cref="Resolve" />. Shared with
    ///     <see cref="CleanupService.FindMissingFiles" /> so the "is this a real file" rule that decides
    ///     whether to split cannot drift from the one that decides whether to fail the call.
    /// </summary>
    public static bool ResolvesToExistingFile(string entry, string solutionDirectory)
    {
        try
        {
            return File.Exists(Resolve(entry, solutionDirectory));
        }
        catch (ArgumentException)
        {
            // A path Path.GetFullPath rejects outright (an embedded null, say) names no file.
            return false;
        }
    }

    /// <summary>
    ///     The fragments <paramref name="entry" /> splits into, or <see langword="null" /> when it must be
    ///     kept verbatim.
    /// </summary>
    private static List<string>? SplitEntry(string entry, string solutionDirectory)
    {
        if (entry.AsSpan().IndexOfAny(Delimiters) < 0) return null;
        if (ResolvesToExistingFile(entry, solutionDirectory)) return null;

        List<string> fragments =
        [
            .. entry.Split(Delimiters)
                .Select(fragment => fragment.Trim())
                .Where(fragment => fragment.Length > 0)
        ];

        return fragments.Count > 0 ? fragments : null;
    }
}