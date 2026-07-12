using System.Text;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Documentation;

/// <summary>
///     Guards the curated external documentation links (JetBrains + StyleCop.Analyzers) that the README
///     and the embedded prompt/resource cite. <see cref="ExtractsCuratedExternalDocLinks" /> is offline and
///     runs on every PR: it fails only if a doc edit drops or malforms a curated link.
///     <see
///         cref="CuratedDocLinks_AreLive" />
///     is a network check gated behind the <c>ExternalLinks</c> trait and
///     runs on a weekly schedule; it is <em>warn-only</em> — a dead third-party page is reported but never
///     fails the build, so a JetBrains docs reorganization cannot turn this repo red.
/// </summary>
public sealed class ExternalDocLinksTests(ITestOutputHelper output)
{
    private const int MaxAttempts = 2;
    private static readonly TimeSpan PerLinkTimeout = TimeSpan.FromSeconds(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ExtractsCuratedExternalDocLinks()
    {
        // Act
        var links = DocLinks.ExtractExternalDocLinks();
        var urls = links.Select(link => link.Url).ToList();

        // Assert — the curated set is present. Guards against a doc edit silently dropping a link, and
        // guarantees the scheduled liveness check never degrades to "0 links, all healthy".
        links.Count.ShouldBeGreaterThanOrEqualTo(12);
        urls.ShouldContain(url => url.Contains("InspectCode.html"));
        urls.ShouldContain(url => url.Contains("CleanupCode.html"));
        urls.ShouldContain(url => url.Contains("stylecop.schema.json"));

        // Assert — the allowlist excludes our own repo links, badges, and the bare-domain trademark link.
        urls.ShouldNotContain("https://www.jetbrains.com");
        urls.ShouldAllBe(url => !url.Contains("github.com/andypgray"));
        urls.ShouldAllBe(url => !url.Contains("img.shields.io"));

        // Assert — every link records at least one citing file, so the stale-link report can point at it.
        links.ShouldAllBe(link => link.SourceFiles.Count > 0);
    }

    [Fact]
    [Trait("Category", "ExternalLinks")]
    public async Task CuratedDocLinks_AreLive()
    {
        // Arrange
        var links = DocLinks.ExtractExternalDocLinks();

        // The only hard assertion: the check actually ran. Staleness below is warn-only, so this is the
        // one way the test goes red — if the extractor or doc structure genuinely breaks.
        links.Count.ShouldBeGreaterThan(0);

        // Assign after construction (not an object initializer) so the handler is always disposed even
        // if a setter threw. AllowAutoRedirect is already the SocketsHttpHandler default; set for intent.
        using SocketsHttpHandler handler = new();
        handler.AllowAutoRedirect = true;
        handler.MaxAutomaticRedirections = 10;
        using HttpClient client = new(handler);
        // JetBrains/Cloudflare answer empty-User-Agent requests with 403; send a real one.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; resharper-cli-mcp-linkcheck/1.0; +https://github.com/andypgray/resharper-cli-mcp)");

        // Act — fan out across the deduped set. Task.WhenAll is awaited before this scope disposes
        // `client`, so the capture is safe (ReSharper's dataflow can't prove it — hence the suppression).
        // ReSharper disable once AccessToDisposedClosure
        var results = await Task.WhenAll(links.Select(link => CheckAsync(client, link, Ct)));

        // Report — warn, don't fail.
        var unhealthy = results
            .Where(result => !result.Healthy)
            .OrderBy(result => result.Link.Url, StringComparer.Ordinal)
            .ToList();

        if (unhealthy.Count == 0)
        {
            output.WriteLine($"All {results.Length} curated documentation link(s) are live.");
            return;
        }

        string report = BuildReport(unhealthy);
        output.WriteLine(report);

        // The scheduled workflow sets LINKCHECK_REPORT and turns the file into a job summary + warning
        // annotations. Reading env is fine here; the "no env mutation in tests" rule bars only writes.
        string? reportPath = Environment.GetEnvironmentVariable("LINKCHECK_REPORT");
        if (!string.IsNullOrWhiteSpace(reportPath)) await File.WriteAllTextAsync(reportPath, report, Ct);
    }

    private static async Task<LinkResult> CheckAsync(
        HttpClient client, ExternalDocLink link, CancellationToken ct)
    {
        var status = "no response";

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(PerLinkTimeout);
                using HttpRequestMessage request = new(HttpMethod.Get, link.Url);

                // ResponseHeadersRead: we only need the status line, not the page body.
                using HttpResponseMessage response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

                var code = (int)response.StatusCode;
                status = $"HTTP {code} {response.ReasonPhrase}".TrimEnd();

                if (code is >= 200 and < 300) return new LinkResult(link, true, status);

                // 5xx and 429 are transient — fall through and retry. 404/410 and other 4xx are definitive.
                if (code is < 500 and not 429) return new LinkResult(link, false, status);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The per-link timeout fired, not the test's own cancellation — retry, then report.
                status = $"timeout after {PerLinkTimeout.TotalSeconds:0}s";
            }
            catch (HttpRequestException ex)
            {
                status = $"request error: {ex.Message}";
            }

        return new LinkResult(link, false, status);
    }

    private static string BuildReport(IReadOnlyList<LinkResult> unhealthy)
    {
        // Explicit '\n' (not AppendLine) keeps the file byte-identical across OSes, so the workflow's
        // line-by-line bash parse is deterministic.
        StringBuilder builder = new();
        builder.Append("## Stale external documentation links\n\n");
        builder.Append(
            $"{unhealthy.Count} curated JetBrains/StyleCop documentation link(s) did not return a healthy ");
        builder.Append(
            "(2xx) response. These are third-party pages outside this repo, so this is a warning, not a ");
        builder.Append("build failure — but the citing docs below may need updating.\n\n");

        foreach (LinkResult result in unhealthy)
        {
            string citedIn = string.Join(", ", result.Link.SourceFiles);
            builder.Append($"- {result.Status} | {result.Link.Url} | cited in: {citedIn}\n");
        }

        return builder.ToString();
    }

    private sealed record LinkResult(ExternalDocLink Link, bool Healthy, string Status);
}