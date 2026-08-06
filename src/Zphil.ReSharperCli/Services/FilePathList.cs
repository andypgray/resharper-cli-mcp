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
            var fragments = SplitEntry(entry, solutionDirectory);

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