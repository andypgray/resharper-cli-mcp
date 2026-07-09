using System.Text.Json;

namespace Zphil.ReSharperCli.Sarif;

/// <summary>
///     Parses jb inspectcode SARIF into flat <see cref="InspectIssue" /> records: one issue per result,
///     taking only the first location and dropping results that have none.
/// </summary>
internal static class SarifParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Parse SARIF JSON content into structured issues (empty list if there are none).</summary>
    public static List<InspectIssue> Parse(string json)
    {
        var report = JsonSerializer.Deserialize<SarifReport>(json, Options);

        List<InspectIssue> issues = [];
        foreach (SarifRun run in report?.Runs ?? [])
        foreach (SarifResult result in run.Results ?? [])
        {
            InspectIssue? issue = ParseResult(result);
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

    private static InspectIssue? ParseResult(SarifResult result)
    {
        SarifPhysicalLocation? location = result.Locations is [{ PhysicalLocation: { } physical }, ..]
            ? physical
            : null;

        if (location?.ArtifactLocation?.Uri is not { } uri) return null;

        string file = uri.StartsWith("file://", StringComparison.Ordinal)
            ? new Uri(uri).LocalPath
            : uri;

        return new InspectIssue(
            file,
            location.Region?.StartLine ?? 0,
            location.Region?.EndLine,
            MapSeverity(result.Level),
            result.RuleId ?? string.Empty,
            result.Message?.Text ?? string.Empty);
    }
}