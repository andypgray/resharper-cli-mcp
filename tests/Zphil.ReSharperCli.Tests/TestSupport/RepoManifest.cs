using System.Text.Json;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Reads one value out of one of the repository's JSON install manifests, addressed by JSON pointer —
///     <c>/description</c>, <c>/packages/0/version</c>. The documentation tests each pin a different
///     property across an overlapping set of those files, so the pointer walk lives here rather than once
///     per test: a second copy is free to read <c>/plugins/0/description</c> differently, and the symptom
///     would be a documentation test failing about the manifest rather than about the walk.
/// </summary>
internal static class RepoManifest
{
    /// <summary>
    ///     Reads the string at <paramref name="jsonPointer" /> from the manifest at
    ///     <paramref name="manifestPath" />, which is relative to the repository root. Returns
    ///     <see langword="null" /> when the property exists but holds JSON <c>null</c>; a pointer naming a
    ///     property that is absent throws, because a test asking for one is asserting it is there.
    /// </summary>
    public static string? ReadString(string manifestPath, string jsonPointer)
    {
        string manifest = File.ReadAllText(Path.Combine(RepoRoot.Location, manifestPath));
        using JsonDocument document = JsonDocument.Parse(manifest);

        return Resolve(document.RootElement, jsonPointer).GetString();
    }

    /// <summary>
    ///     Walks a JSON pointer — <c>/packages/0/version</c> — from <paramref name="root" />, indexing an
    ///     array when a segment is a number and reading a property otherwise.
    /// </summary>
    private static JsonElement Resolve(JsonElement root, string jsonPointer)
    {
        JsonElement current = root;

        foreach (string segment in jsonPointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
            current = int.TryParse(segment, out int index)
                ? current[index]
                : current.GetProperty(segment);

        return current;
    }
}