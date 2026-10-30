using System.Diagnostics.CodeAnalysis;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Services;

/// <summary>
///     Normalizes the <c>files</c> argument and classifies its entries.
/// </summary>
/// <remarks>
///     <para>
///         A caller that joins several paths into one array element (<c>["a.cs, b.cs"]</c> rather than
///         <c>["a.cs", "b.cs"]</c>) makes a mistake the array parameter invites, and the tools fail on it
///         differently: cleanup rejects the joined string as a missing file, while inspect hands it to
///         <c>jb</c>, matches nothing, and reports "No issues found.", a false negative. Splitting the element
///         at the tool edge makes both work.
///     </para>
///     <para>
///         It is also the one home of the rules that classify an entry (a wildcard for <c>jb</c> to expand or
///         not, a file that is there or not), so no two tools can disagree about the same argument.
///     </para>
///     <para>
///         Splitting and translation apply at different depths. Splitting happens at the tool edge, before
///         validation, so the error names the fragment that is wrong. Translating an entry into the spelling
///         <c>jb</c> matches (<see cref="ToIncludePattern" />, or <see cref="ToFilterIncludePattern" /> through a
///         solution filter) happens at the <c>--include</c> boundary, so the caller's own path is what cleanup
///         echoes back in its report.
///     </para>
/// </remarks>
internal static class FilePathList
{
    private static readonly char[] Delimiters = [';', ','];

    /// <summary>
    ///     Splits any entry that joins several paths into separate entries, resolving relative fragments
    ///     against <paramref name="solutionDirectory" /> only to decide whether an entry is already a real
    ///     file.
    /// </summary>
    /// <remarks>
    ///     Returns <paramref name="files" /> itself when nothing needed splitting. An entry that names an
    ///     existing file is kept verbatim, so a legitimate <c>Foo,Bar.cs</c> on disk is never reinterpreted.
    ///     That guard is what makes this safe for the destructive tool: a split can only ever turn a call that
    ///     was guaranteed to fail into one that works. An entry that is nothing but delimiters is also kept
    ///     verbatim, so the existing validation reports what the caller sent.
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
    ///     Returns the absolute path <paramref name="entry" /> resolves to against
    ///     <paramref name="solutionDirectory" /> (an absolute entry ignores it).
    /// </summary>
    /// <remarks>
    ///     The one spelling of the resolution rule, so a change to how an entry maps to a file cannot leave its
    ///     users pointing at different paths.
    /// </remarks>
    public static string Resolve(string entry, string solutionDirectory)
    {
        return Path.GetFullPath(entry, solutionDirectory);
    }

    /// <summary>
    ///     Returns the spelling of <paramref name="entry" /> that <c>jb</c>'s <c>--include</c> can match:
    ///     relative to <paramref name="solutionDirectory" />, forward-slashed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The one spelling of the jb-pattern rule, as <see cref="Resolve" /> is the one spelling of the
    ///         filesystem rule. It never throws: it runs on the way to <c>jb</c>, after validation has already
    ///         judged the entry, so an entry the runtime rejects outright is passed through verbatim rather than
    ///         failing a call that validation let through.
    ///     </para>
    ///     <para>
    ///         <c>--include</c> takes "a set of relative paths" and wildcards, per <c>jb</c>'s own help text,
    ///         and matches them against the solution model rather than against the disk, so an absolute entry
    ///         becomes an Ant pattern that matches nothing. Both subcommands refuse one, in ways that vary by
    ///         release: <c>cleanupcode</c> exits 3 with "No items were found to cleanup", while
    ///         <c>inspectcode</c> exited 0 and reported no issues at all, a silent false negative, through
    ///         2026.1, and exits 3 with "No files to inspect were found." in 2026.2. Both tools document an
    ///         absolute path as accepted, so this translation is what makes that true rather than a new
    ///         restriction.
    ///     </para>
    ///     <para>
    ///         Fully qualified rather than rooted: on Windows a drive-relative <c>/src/Foo.cs</c> is already the
    ///         relative form <c>jb</c> wants, and relativising it would turn it into <c>../src/Foo.cs</c>. An
    ///         entry outside the solution directory becomes a <c>../</c> form, which is still the relative path
    ///         <c>jb</c> asked for (projects above the solution file are a legitimate layout), and an entry on
    ///         another volume has no relative form, so <see cref="Path.GetRelativePath" /> returns it unchanged,
    ///         which is the correct verbatim fallback.
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
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            // The same entries ResolvesToExistingFile answers false for. An entry past the operating system's
            // path limit surfaces from Windows as PathTooLongException, an IOException.
            return entry;
        }
    }

    /// <summary>
    ///     Returns the spelling of <paramref name="entry" /> that <c>jb</c>'s <c>--include</c> matches through a
    ///     solution filter stored outside the solution's directory: <c>**/</c> and then the absolute path without
    ///     its root, forward-slashed.
    /// </summary>
    /// <remarks>
    ///     Through such a filter <c>jb</c> matches neither a path relative to the solution nor a <c>../</c> path
    ///     relative to the filter, while <c>**/</c> followed by the whole path matches the one file and nothing
    ///     else. The root is left out so that one spelling serves a drive letter, a UNC share and <c>/</c> alike.
    ///     The entry has to name a file, as every entry does by the time a filter is chosen.
    /// </remarks>
    public static string ToFilterIncludePattern(string entry, string solutionDirectory)
    {
        string resolved = Resolve(entry, solutionDirectory);
        string root = Path.GetPathRoot(resolved) ?? string.Empty;
        string belowRoot = resolved[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return "**/" + belowRoot.Replace('\\', '/');
    }

    /// <summary>
    ///     Determines whether a <c>files</c> entry is a wildcard pattern, handed to <c>jb</c> unexpanded and
    ///     never a single file: one that contains <c>*</c>, <c>?</c>, or <c>[</c>.
    /// </summary>
    public static bool IsPattern(string entry)
    {
        return entry.AsSpan().IndexOfAny('*', '?', '[') >= 0;
    }

    /// <summary>
    ///     Returns the entries in <paramref name="files" /> that do not resolve to an existing file.
    /// </summary>
    /// <remarks>
    ///     Wildcard patterns (see <see cref="IsPattern" />) are left for jb to expand and are never reported;
    ///     other entries are resolved against <paramref name="solutionDirectory" /> (absolute entries ignore
    ///     it). A blank entry is reported as missing rather than throwing, so the caller is told which entry it
    ///     could not use: <see cref="ResolvesToExistingFile" /> catches what
    ///     <see cref="Path.GetFullPath(string,string)" /> raises on the empty string and answers false.
    /// </remarks>
    public static List<string> FindMissing(IReadOnlyList<string> files, string solutionDirectory)
    {
        List<string> missing = [];
        foreach (string entry in files)
        {
            if (IsPattern(entry)) continue;

            if (!ResolvesToExistingFile(entry, solutionDirectory)) missing.Add(entry);
        }

        return missing;
    }

    /// <summary>
    ///     Determines whether <paramref name="entry" /> names a file that exists, per <see cref="Resolve" />.
    /// </summary>
    /// <remarks>
    ///     Shared with <see cref="FindMissing" /> so the "is this a real file" rule that decides whether to
    ///     split cannot drift from the one that decides whether to fail the call.
    /// </remarks>
    public static bool ResolvesToExistingFile(string entry, string solutionDirectory)
    {
        try
        {
            return File.Exists(Resolve(entry, solutionDirectory));
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            // A path Path.GetFullPath rejects outright names no file — an embedded null as an
            // ArgumentException, an entry past the operating system's path limit as PathTooLongException.
            return false;
        }
    }

    /// <summary>
    ///     Returns the fragments <paramref name="entry" /> splits into, or <see langword="null" /> when it must
    ///     be kept verbatim.
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