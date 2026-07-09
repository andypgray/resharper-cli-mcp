namespace Zphil.ReSharperCli.Sarif;

// Minimal SARIF 2.1.0 shape needed to read jb inspectcode output. Every field jb emits that we do not
// use (rules, taxa, invocations, artifacts, partialFingerprints, region columns, …) is simply ignored
// by System.Text.Json. Deserialized with JsonSerializerDefaults.Web, so camelCase JSON binds to these
// PascalCase members case-insensitively.

internal sealed record SarifReport(List<SarifRun>? Runs);

internal sealed record SarifRun(List<SarifResult>? Results);

internal sealed record SarifResult(
    string? RuleId,
    string? Level,
    SarifMessage? Message,
    List<SarifLocation>? Locations);

internal sealed record SarifMessage(string? Text);

internal sealed record SarifLocation(SarifPhysicalLocation? PhysicalLocation);

internal sealed record SarifPhysicalLocation(SarifArtifactLocation? ArtifactLocation, SarifRegion? Region);

internal sealed record SarifArtifactLocation(string? Uri);

internal sealed record SarifRegion(int? StartLine, int? EndLine);

/// <summary>
///     A single inspection issue, flattened from one SARIF result's first location and ready for the
///     markdown formatter.
/// </summary>
internal sealed record InspectIssue(
    string File,
    int Line,
    int? EndLine,
    string Severity,
    string RuleId,
    string Message);