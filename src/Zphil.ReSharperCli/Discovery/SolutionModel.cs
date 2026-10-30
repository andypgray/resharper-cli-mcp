using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Discovery;

/// <summary>
///     Represents one project a solution lists: its path as the solution spells it, and the absolute path that
///     spelling resolves to.
/// </summary>
/// <remarks>
///     The spelling is kept because a solution filter names its projects the way the solution does, and that is
///     the form <c>jb</c> matches a filter's entries against.
/// </remarks>
internal sealed record SolutionProject(string Spelling, string FullPath);

/// <summary>
///     Represents what a read of the project model produced: a value, or the reason it declined to produce one.
/// </summary>
/// <remarks>
///     A decline is not an error. The model could not be read well enough to act on, and
///     <see cref="Declined" /> is a clause stating why.
/// </remarks>
internal sealed record ModelReading<T>(T? Value, string? Declined) where T : class
{
    public static ModelReading<T> Of(T value)
    {
        return new ModelReading<T>(value, null);
    }

    public static ModelReading<T> Decline(string reason)
    {
        return new ModelReading<T>(null, reason);
    }
}

/// <summary>Reads the list of projects a <c>.sln</c> or <c>.slnx</c> holds.</summary>
internal static partial class SolutionModel
{
    /// <summary>
    ///     Gets how two paths in the solution's tree are compared, with the operating system standing in for the
    ///     filesystem's case rules.
    /// </summary>
    internal static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    ///     Returns every project <paramref name="solutionPath" /> lists, in the order it lists them, or why they
    ///     could not be read.
    /// </summary>
    /// <remarks>
    ///     A <c>.slnx</c> lists a project as a <c>Project</c> element with a <c>Path</c> attribute at any depth,
    ///     since solution folders nest. A <c>.sln</c> lists solution folders on the same <c>Project(...)</c> lines
    ///     as projects, so only an entry whose path ends in a project extension (<c>.csproj</c>, <c>.vbproj</c>,
    ///     and the rest of the <c>*proj</c> family) counts.
    /// </remarks>
    public static ModelReading<IReadOnlyList<SolutionProject>> ReadProjects(string solutionPath)
    {
        string solutionDirectory = Path.GetDirectoryName(solutionPath)!;

        List<string> spellings;
        try
        {
            if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                spellings = ReadSlnx(solutionPath);
            else if (solutionPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                spellings = ReadSln(solutionPath);
            else
                return ModelReading<IReadOnlyList<SolutionProject>>.Decline($"{Path.GetFileName(solutionPath)} is neither a .sln nor a .slnx");
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception) || exception is XmlException)
        {
            return ModelReading<IReadOnlyList<SolutionProject>>.Decline($"the solution file could not be read ({exception.Message})");
        }

        if (spellings.Count == 0) return ModelReading<IReadOnlyList<SolutionProject>>.Decline("the solution file lists no projects");

        List<SolutionProject> projects = spellings
            .Select(spelling => new SolutionProject(spelling, ResolveSpelling(spelling, solutionDirectory)))
            .DistinctBy(project => project.FullPath, PathComparer)
            .ToList();

        return ModelReading<IReadOnlyList<SolutionProject>>.Of(projects);
    }

    /// <summary>
    ///     Resolves a path as an MSBuild file writes it, with either separator, against
    ///     <paramref name="baseDirectory" />.
    /// </summary>
    /// <remarks>
    ///     A file written on Windows spells paths with backslashes, which a Unix path would keep as part of a file
    ///     name.
    /// </remarks>
    internal static string ResolveSpelling(string spelling, string baseDirectory)
    {
        string portable = spelling.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(portable, baseDirectory);
    }

    private static List<string> ReadSlnx(string solutionPath)
    {
        using FileStream stream = File.OpenRead(solutionPath);
        XDocument document = XDocument.Load(stream);

        return document.Descendants()
            .Where(element => element.Name.LocalName == "Project")
            .Select(element => element.Attribute("Path")?.Value)
            .OfType<string>()
            .Where(path => path.Length > 0)
            .ToList();
    }

    private static List<string> ReadSln(string solutionPath)
    {
        return File.ReadLines(solutionPath)
            .Select(line => SlnProjectLine().Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups["path"].Value)
            .Where(path => Path.GetExtension(path).EndsWith("proj", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // A .sln project line, Project("{type guid}") = "Name", "Path", "{project guid}", capturing Path.
    [GeneratedRegex("""^\s*Project\("[^"]*"\)\s*=\s*"[^"]*"\s*,\s*"(?<path>[^"]+)"\s*,""")]
    private static partial Regex SlnProjectLine();
}