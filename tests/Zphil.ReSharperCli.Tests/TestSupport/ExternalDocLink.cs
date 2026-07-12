namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     A curated external documentation link and the repo-relative markdown files that cite it.
/// </summary>
/// <param name="Url">The absolute http(s) URL, with trailing sentence punctuation trimmed.</param>
/// <param name="SourceFiles">Repo-relative paths of the markdown files that reference the URL.</param>
internal sealed record ExternalDocLink(string Url, IReadOnlyList<string> SourceFiles);