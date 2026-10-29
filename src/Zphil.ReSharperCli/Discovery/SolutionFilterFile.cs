using System.Text.Json;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Discovery;

/// <summary>
///     Reads a solution filter (<c>.slnf</c>): the JSON file naming a solution and the subset of its projects to
///     load.
/// </summary>
/// <remarks>
///     <c>jb</c> takes a filter wherever it takes a solution, and mounts the settings of the solution the filter
///     names rather than any beside the filter itself, so this server has to follow the same pointer to find the
///     <c>.DotSettings</c> and the cleanup profile <c>jb</c> will use.
/// </remarks>
internal static class SolutionFilterFile
{
    private const string Extension = ".slnf";

    // A filter is often edited by hand, so a trailing comma or a comment is read past rather than failing the call.
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>Determines whether <paramref name="path" /> names a solution filter, by its extension.</summary>
    public static bool IsFilter(string path)
    {
        return path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Returns the absolute path of the solution the filter at <paramref name="filterPath" /> names: its
    ///     <c>solution.path</c>, resolved against the filter's own directory unless it is already absolute.
    /// </summary>
    /// <exception cref="UserErrorException">
    ///     The filter cannot be read or parsed, names no solution, or names one that does not exist. Each is a
    ///     filter <c>jb</c> could not load either, so failing here names the file before a run is spent on it.
    /// </exception>
    public static string ReadSolutionPath(string filterPath)
    {
        string? declared;
        try
        {
            using FileStream stream = File.OpenRead(filterPath);
            using JsonDocument document = JsonDocument.Parse(stream, ReadOptions);

            declared = document.RootElement.ValueKind == JsonValueKind.Object
                       && document.RootElement.TryGetProperty("solution", out JsonElement solution)
                       && solution.ValueKind == JsonValueKind.Object
                       && solution.TryGetProperty("path", out JsonElement path)
                       && path.ValueKind == JsonValueKind.String
                ? path.GetString()
                : null;
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception) || exception is JsonException)
        {
            throw new UserErrorException($"The solution filter \"{filterPath}\" could not be read: {exception.Message}", exception);
        }

        if (string.IsNullOrWhiteSpace(declared))
            throw new UserErrorException($"The solution filter \"{filterPath}\" names no solution: it has no \"solution.path\" string.");

        // A filter written on Windows spells the path with backslashes, which a Unix path would keep as part of a
        // file name.
        string portable = declared.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        string resolved = Path.GetFullPath(portable, Path.GetDirectoryName(filterPath)!);
        if (!File.Exists(resolved))
            throw new UserErrorException(
                $"The solution filter \"{filterPath}\" names the solution \"{declared}\", which does not exist (resolved to \"{resolved}\").");

        return resolved;
    }
}