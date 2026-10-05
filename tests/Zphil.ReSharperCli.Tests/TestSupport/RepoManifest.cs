using System.Text.Json;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Reads one value out of one of the repository's JSON install manifests, addressed by JSON pointer —
///     <c>/description</c>, <c>/packages/0/version</c>.
/// </summary>
/// <remarks>
///     The pointer walk lives here rather than once per test: a second copy is free to read
///     <c>/plugins/0/description</c> differently, and the symptom would be a test failing about the manifest
///     rather than about the walk.
/// </remarks>
internal static class RepoManifest
{
    /// <summary>
    ///     Reads the string at <paramref name="jsonPointer" /> from the manifest at
    ///     <paramref name="manifestPath" />, which is relative to the repository root.
    /// </summary>
    /// <remarks>
    ///     Returns <see langword="null" /> when the property exists but holds JSON <c>null</c>; a pointer naming
    ///     a property that is absent throws, because a test asking for one is asserting it is there.
    /// </remarks>
    public static string? ReadString(string manifestPath, string jsonPointer)
    {
        return Read(manifestPath, jsonPointer).GetString();
    }

    /// <summary>
    ///     The value at <paramref name="jsonPointer" />, for a test that walks an array or an object from there.
    /// </summary>
    /// <remarks>
    ///     Detached from the parsed document, so it outlives this call. Throws when the pointer names nothing.
    /// </remarks>
    public static JsonElement Read(string manifestPath, string jsonPointer)
    {
        using JsonDocument document = Parse(manifestPath);

        if (!TryResolve(document.RootElement, jsonPointer, out JsonElement value))
            throw new KeyNotFoundException($"{manifestPath} has nothing at {jsonPointer}.");

        return value.Clone();
    }

    /// <summary>Whether the manifest has anything at <paramref name="jsonPointer" />, JSON <c>null</c> included.</summary>
    public static bool Has(string manifestPath, string jsonPointer)
    {
        using JsonDocument document = Parse(manifestPath);

        return TryResolve(document.RootElement, jsonPointer, out _);
    }

    private static JsonDocument Parse(string manifestPath)
    {
        string manifest = RepoRoot.ReadText(manifestPath);
        return JsonDocument.Parse(manifest);
    }

    /// <summary>
    ///     Walks a JSON pointer — <c>/packages/0/version</c> — from <paramref name="root" />, indexing an
    ///     array when a segment is a number and reading a property otherwise.
    /// </summary>
    private static bool TryResolve(JsonElement root, string jsonPointer, out JsonElement value)
    {
        JsonElement current = root;
        value = default;

        foreach (string segment in jsonPointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(segment, out int index))
            {
                if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength()) return false;

                current = current[index];
            }
            else if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return false;
            }

        value = current;
        return true;
    }
}