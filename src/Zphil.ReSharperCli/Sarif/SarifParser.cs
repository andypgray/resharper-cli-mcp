using System.Text.Json;

namespace Zphil.ReSharperCli.Sarif;

/// <summary>
///     Parses jb inspectcode SARIF into flat <see cref="InspectIssue" /> records: one issue per result,
///     taking only the first location and dropping results that have none.
/// </summary>
internal static class SarifParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     Parse SARIF JSON from <paramref name="sarif" /> into structured issues (empty list if there are
    ///     none). Deserializing straight off the stream matters at this input's scale: a solution-wide run's
    ///     SARIF is multi-megabyte, and reading it into a string first would transiently hold the bytes, the
    ///     UTF-16 copy, and the transcode back to UTF-8 all at once.
    /// </summary>
    public static async Task<List<InspectIssue>> ParseAsync(Stream sarif, CancellationToken cancellationToken)
    {
        var report = await JsonSerializer.DeserializeAsync<SarifReport>(sarif, Options, cancellationToken);

        return ExtractIssues(report);
    }

    /// <summary>Parse SARIF JSON content into structured issues (empty list if there are none).</summary>
    public static List<InspectIssue> Parse(string json)
    {
        var report = JsonSerializer.Deserialize<SarifReport>(json, Options);

        return ExtractIssues(report);
    }

    private static List<InspectIssue> ExtractIssues(SarifReport? report)
    {
        // Issues repeat the same few hundred file URIs thousands of times; converting each distinct URI once
        // also lets every issue in a file share one path string instance.
        Dictionary<string, string> fileByUri = new(StringComparer.Ordinal);

        List<InspectIssue> issues = [];
        foreach (SarifRun run in report?.Runs ?? [])
        foreach (SarifResult result in run.Results ?? [])
        {
            InspectIssue? issue = ParseResult(result, fileByUri);
            if (issue is not null) issues.Add(issue);
        }

        return issues;
    }

    /// <summary>Map a SARIF severity level to the label surfaced to the client.</summary>
    public static string MapSeverity(string? level)
    {
        return level switch
        {
            "error" => "ERROR",
            "warning" => "WARNING",
            "note" => "SUGGESTION",
            _ => level?.ToUpperInvariant() ?? string.Empty
        };
    }

    private static InspectIssue? ParseResult(SarifResult result, Dictionary<string, string> fileByUri)
    {
        SarifPhysicalLocation? location = result.Locations is [{ PhysicalLocation: { } physical }, ..]
            ? physical
            : null;

        if (location?.ArtifactLocation?.Uri is not { } uri) return null;

        if (!fileByUri.TryGetValue(uri, out string? file))
        {
            // Tried rather than assumed: a file:// string the runtime rejects (a malformed host, an embedded
            // null) is bad input from jb, not a fault here, and constructing a Uri from it would throw past
            // the JsonException the caller catches — surfacing minutes of finished work as a bug in this
            // server. Left verbatim instead, exactly as the non-file:// branch below leaves its input.
            file = uri.StartsWith("file://", StringComparison.Ordinal)
                   && Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed)
                ? parsed.LocalPath
                : uri;
            fileByUri[uri] = file;
        }

        return new InspectIssue(
            file,
            location.Region?.StartLine ?? 0,
            location.Region?.EndLine,
            MapSeverity(result.Level),
            result.RuleId ?? string.Empty,
            result.Message?.Text ?? string.Empty);
    }
}