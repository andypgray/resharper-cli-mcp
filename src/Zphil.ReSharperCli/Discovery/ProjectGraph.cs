using System.Xml;
using System.Xml.Linq;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Discovery;

/// <summary>
///     Reads which of a solution's projects a set of projects needs loaded beside it: the transitive closure of
///     their <c>ProjectReference</c> items.
/// </summary>
/// <remarks>
///     <para>
///         Read from the XML, not evaluated. Conditions are ignored, so a reference that only some configuration
///         takes is followed anyway: a project loaded needlessly costs a little time, while one left out is
///         resolved by <c>jb</c> from its build output, which a stale build makes wrong. A reference to a
///         project outside the solution is skipped, because a solution-wide run would not load it either.
///     </para>
///     <para>
///         A project's references can be declared outside its own file, so the closure also reads every
///         <c>Directory.Build.props</c> and <c>Directory.Build.targets</c> on the project's ancestor chain (all of
///         them, a superset of the nearest one MSBuild imports), and every file one of those or the project
///         imports by a path this reader can resolve: a literal path, or one built from
///         <c>$(MSBuildThisFileDirectory)</c> or <c>$(MSBuildProjectDirectory)</c>. A relative reference found in
///         any of them resolves against the project's directory, as MSBuild resolves an item. An import whose path
///         needs any other property is not followed. That is the shape of a NuGet package's targets, which declare
///         no project references, and declining on it would turn the filter off for any repository that wires a
///         package by hand.
///     </para>
///     <para>
///         A reference this reader cannot resolve, one whose <c>Include</c> needs a property, an item list or a
///         wildcard, declines the whole closure rather than guessing, and so does a file that will not parse.
///     </para>
/// </remarks>
internal static class ProjectGraph
{
    private static readonly string[] DirectoryBuildFileNames = ["Directory.Build.props", "Directory.Build.targets"];

    private static readonly string[] UnevaluatedMarkers = ["$(", "@(", "%(", "*", "?"];

    /// <summary>
    ///     Returns <paramref name="roots" /> and every project of <paramref name="solution" /> they reach through
    ///     <c>ProjectReference</c> items, in the solution's order, or why the references could not be read.
    /// </summary>
    public static ModelReading<IReadOnlyList<SolutionProject>> Closure(
        IReadOnlyList<SolutionProject> solution,
        IEnumerable<SolutionProject> roots)
    {
        Dictionary<string, SolutionProject> byPath = solution.ToDictionary(project => project.FullPath, SolutionModel.PathComparer);
        HashSet<string> reached = new(SolutionModel.PathComparer);
        Queue<SolutionProject> pending = new();

        foreach (SolutionProject root in roots)
            if (reached.Add(root.FullPath))
                pending.Enqueue(root);

        while (pending.TryDequeue(out SolutionProject? project))
        {
            ModelReading<List<string>> references = ReferencesOf(project.FullPath);
            if (references.Value is null) return ModelReading<IReadOnlyList<SolutionProject>>.Decline(references.Declined!);

            foreach (string reference in references.Value)
                if (byPath.TryGetValue(reference, out SolutionProject? referenced) && reached.Add(reference))
                    pending.Enqueue(referenced);
        }

        List<SolutionProject> closure = solution.Where(project => reached.Contains(project.FullPath)).ToList();
        return ModelReading<IReadOnlyList<SolutionProject>>.Of(closure);
    }

    /// <summary>Returns the absolute paths of every project <paramref name="projectPath" /> references.</summary>
    private static ModelReading<List<string>> ReferencesOf(string projectPath)
    {
        string projectDirectory = Path.GetDirectoryName(projectPath)!;
        ReferenceScan scan = new(projectDirectory);

        scan.Visit(projectPath);

        for (DirectoryInfo? directory = new(projectDirectory); directory is not null; directory = directory.Parent)
            foreach (string name in DirectoryBuildFileNames)
            {
                string candidate = Path.Combine(directory.FullName, name);
                if (File.Exists(candidate)) scan.Visit(candidate);
            }

        return scan.Declined is { } reason
            ? ModelReading<List<string>>.Decline(reason)
            : ModelReading<List<string>>.Of(scan.References);
    }

    /// <summary>Determines whether <paramref name="value" /> still needs MSBuild to evaluate it.</summary>
    private static bool IsUnevaluated(string value)
    {
        return UnevaluatedMarkers.Any(marker => value.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>
    ///     Collects the references one project's files declare, following imports, and stops at the first
    ///     reference it cannot resolve.
    /// </summary>
    private sealed class ReferenceScan(string projectDirectory)
    {
        private readonly HashSet<string> _visited = new(SolutionModel.PathComparer);

        public List<string> References { get; } = [];

        public string? Declined { get; private set; }

        public void Visit(string file)
        {
            if (Declined is not null || !_visited.Add(file)) return;

            XDocument document;
            try
            {
                using FileStream stream = File.OpenRead(file);
                document = XDocument.Load(stream);
            }
            catch (Exception exception) when (FilesystemFailure.Covers(exception) || exception is XmlException)
            {
                Declined = $"{file} could not be read ({exception.Message})";
                return;
            }

            string fileDirectory = Path.GetDirectoryName(file)!;
            foreach (XElement element in document.Descendants())
            {
                if (element.Name.LocalName == "ProjectReference")
                    AddReferences(element.Attribute("Include")?.Value, file, fileDirectory);
                else if (element.Name.LocalName == "Import" && element.Attribute("Sdk") is null)
                    FollowImport(element.Attribute("Project")?.Value, fileDirectory);

                if (Declined is not null) return;
            }
        }

        private void AddReferences(string? include, string file, string fileDirectory)
        {
            // An Update or Remove names no new reference, and dropping one only narrows what is needed.
            if (include is null) return;

            foreach (string entry in include.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                string expanded = Expand(entry, fileDirectory);
                if (IsUnevaluated(expanded))
                {
                    Declined = $"{file} references a project as \"{entry}\", which needs MSBuild to evaluate";
                    return;
                }

                References.Add(SolutionModel.ResolveSpelling(expanded, projectDirectory));
            }
        }

        private void FollowImport(string? project, string fileDirectory)
        {
            if (project is null) return;

            string expanded = Expand(project, fileDirectory);
            if (IsUnevaluated(expanded)) return;

            // An import path is relative to the file that imports it, unlike an item's.
            string resolved = SolutionModel.ResolveSpelling(expanded, fileDirectory);
            if (File.Exists(resolved)) Visit(resolved);
        }

        private string Expand(string value, string fileDirectory)
        {
            return value
                .Replace("$(MSBuildThisFileDirectory)", fileDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                .Replace("$(MSBuildProjectDirectory)", projectDirectory, StringComparison.OrdinalIgnoreCase);
        }
    }
}