namespace Zphil.ReSharperCli.Sarif;

// Minimal SARIF 2.1.0 shape needed to read jb inspectcode output. Every field jb emits that we do not
// use (rules, taxa, invocations, artifacts, partialFingerprints, region columns, …) is simply ignored
// by System.Text.Json. Deserialized with JsonSerializerDefaults.Web, so camelCase JSON binds to these
// PascalCase members case-insensitively.
//
// artifacts is left out on evidence rather than by default. It is the one field that could vouch for a file
// that was inspected and drew nothing, but measured on jb 2026.2.3.1 it lists only the files the results
// already name, so it carries nothing they do not. The JbContract suite reports if that changes.

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
///     A single inspection issue, flattened from one SARIF result's first location.
/// </summary>
internal sealed record InspectIssue(
    string File,
    int Line,
    int? EndLine,
    string Severity,
    string RuleId,
    string Message);