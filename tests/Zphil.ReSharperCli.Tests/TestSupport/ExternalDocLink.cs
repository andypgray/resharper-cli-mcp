namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Represents a curated external documentation link, an absolute http or https URL with trailing sentence
///     punctuation trimmed, and the repo-relative markdown files that cite it.
/// </summary>
internal sealed record ExternalDocLink(string Url, IReadOnlyList<string> SourceFiles);