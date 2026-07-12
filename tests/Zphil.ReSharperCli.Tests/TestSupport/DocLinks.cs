using System.Text.RegularExpressions;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Extracts the curated set of external documentation links (JetBrains + StyleCop.Analyzers) that the
///     repo's markdown cites, so a test can assert the set is intact (offline) and, on a schedule, that
///     each URL is still live. Pure, offline, deterministic — no network, no environment reads — so it is
///     safe under the parallel runner.
/// </summary>
internal static partial class DocLinks
{
    // Permissive on purpose: embedded docs use autolinks <https://…> while the README uses inline
    // [text](https://…). The char class stops at the delimiters of both forms plus whitespace and
    // quotes; trailing sentence punctuation is trimmed afterwards.
    [GeneratedRegex(@"https?://[^\s)>\]""']+")]
    private static partial Regex UrlPattern();

    /// <summary>All <c>*.md</c> files under the repo root, excluding build output and VCS directories.</summary>
    public static IReadOnlyList<string> EnumerateMarkdown()
    {
        return Directory
            .EnumerateFiles(RepoRoot.Location, "*.md", SearchOption.AllDirectories)
            .Where(path => !IsExcludedDirectory(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    ///     The deduped curated external doc links, each paired with the repo-relative files that cite it.
    ///     Applies the host/path allowlist that keeps only JetBrains help/blog pages and StyleCop docs.
    /// </summary>
    public static IReadOnlyList<ExternalDocLink> ExtractExternalDocLinks()
    {
        Dictionary<string, SortedSet<string>> citingFilesByUrl = new(StringComparer.Ordinal);

        foreach (string file in EnumerateMarkdown())
        {
            string relativePath = Path.GetRelativePath(RepoRoot.Location, file).Replace('\\', '/');
            string content = File.ReadAllText(file);

            foreach (Match match in UrlPattern().Matches(content))
            {
                string url = TrimTrailingPunctuation(match.Value);
                if (!IsCuratedDocLink(url)) continue;

                if (!citingFilesByUrl.TryGetValue(url, out var citingFiles))
                {
                    citingFiles = new SortedSet<string>(StringComparer.Ordinal);
                    citingFilesByUrl[url] = citingFiles;
                }

                citingFiles.Add(relativePath);
            }
        }

        return citingFilesByUrl
            .Select(entry => new ExternalDocLink(entry.Key, entry.Value.ToList()))
            .OrderBy(link => link.Url, StringComparer.Ordinal)
            .ToList();
    }

    // Keep only the third-party doc links we curate: JetBrains help/marketing pages (but not the
    // bare-domain trademark link, whose path is just "/") and the blog, plus StyleCop.Analyzers docs and
    // schema. Anchoring GitHub to /DotNetAnalyzers/ naturally excludes our own github.com/andypgray links.
    private static bool IsCuratedDocLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;

        return uri.Host switch
        {
            "www.jetbrains.com" => uri.AbsolutePath.Length > 1,
            "blog.jetbrains.com" => true,
            "github.com" or "raw.githubusercontent.com" =>
                uri.AbsolutePath.StartsWith("/DotNetAnalyzers/", StringComparison.Ordinal),
            _ => false
        };
    }

    private static string TrimTrailingPunctuation(string url)
    {
        return url.TrimEnd('.', ',', ';', ':', '!', '?');
    }

    private static bool IsExcludedDirectory(string path)
    {
        string relativePath = Path.GetRelativePath(RepoRoot.Location, path);
        string[] segments = relativePath.Split(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => segment is "bin" or "obj" or ".git");
    }
}