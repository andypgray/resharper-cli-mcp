using System.Text.RegularExpressions;
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
///         imports. A relative reference found in any of them resolves against the project's directory, as
///         MSBuild resolves an item; an import's path resolves against the file that imports it.
///     </para>
///     <para>
///         A path may use <c>$(MSBuildThisFileDirectory)</c>, <c>$(MSBuildProjectDirectory)</c>,
///         <c>$(SolutionDir)</c>, and any property a <c>PropertyGroup</c> in the files read defines. Every value a
///         property is given anywhere counts, conditions ignored, so a path that names one expands to each, and
///         the order in which the files define and use a property does not matter.
///     </para>
///     <para>
///         An import whose path still needs evaluating is not followed when it starts at a package or toolset
///         folder (<see cref="OutOfTreeRoots" />), or when a file the ancestor walk reads uses it to look up a
///         <c>Directory.Build</c> file, which the walk reads anyway. Any other one declines the whole closure,
///         because a reference declared inside it could not be seen. So does a reference whose <c>Include</c>
///         still needs a property, an item list or a wildcard, and so does a file that will not parse.
///     </para>
/// </remarks>
internal static partial class ProjectGraph
{
    /// <summary>How many property references one path may expand through before it counts as unevaluated.</summary>
    /// <remarks>Bounds a property defined in terms of itself, which would otherwise expand forever.</remarks>
    private const int MaxSubstitutions = 32;

    /// <summary>How many distinct paths one spelling may expand to before it counts as unevaluated.</summary>
    private const int MaxCandidates = 64;

    private static readonly string[] DirectoryBuildFileNames = ["Directory.Build.props", "Directory.Build.targets"];

    private static readonly string[] UnevaluatedMarkers = ["$(", "@(", "%(", "*", "?"];

    /// <summary>
    ///     The properties that name the NuGet package folder or the MSBuild toolset, neither of which holds a
    ///     solution's projects, so an import under one declares no reference this closure needs.
    /// </summary>
    private static readonly string[] OutOfTreeRoots =
    [
        "NuGetPackageRoot",
        "MSBuildExtensionsPath",
        "MSBuildExtensionsPath32",
        "MSBuildExtensionsPath64",
        "MSBuildToolsPath",
        "MSBuildBinPath",
        "MSBuildSDKsPath",
        "VSToolsPath"
    ];

    /// <summary>
    ///     Returns <paramref name="roots" /> and every project of <paramref name="solution" /> they reach through
    ///     <c>ProjectReference</c> items, in the solution's order, or why the references could not be read.
    ///     <paramref name="solutionDirectory" /> is the directory <c>$(SolutionDir)</c> names.
    /// </summary>
    public static ModelReading<IReadOnlyList<SolutionProject>> Closure(
        IReadOnlyList<SolutionProject> solution,
        IEnumerable<SolutionProject> roots,
        string solutionDirectory)
    {
        Dictionary<string, SolutionProject> byPath = solution.ToDictionary(project => project.FullPath, SolutionModel.PathComparer);
        HashSet<string> reached = new(SolutionModel.PathComparer);
        Queue<SolutionProject> pending = new();

        foreach (SolutionProject root in roots)
            if (reached.Add(root.FullPath))
                pending.Enqueue(root);

        while (pending.TryDequeue(out SolutionProject? project))
        {
            ModelReading<List<string>> references = ReferencesOf(project.FullPath, solutionDirectory);
            if (references.Value is null) return ModelReading<IReadOnlyList<SolutionProject>>.Decline(references.Declined!);

            foreach (string reference in references.Value)
                if (byPath.TryGetValue(reference, out SolutionProject? referenced) && reached.Add(reference))
                    pending.Enqueue(referenced);
        }

        List<SolutionProject> closure = solution.Where(project => reached.Contains(project.FullPath)).ToList();
        return ModelReading<IReadOnlyList<SolutionProject>>.Of(closure);
    }

    /// <summary>Returns the absolute paths of every project <paramref name="projectPath" /> references.</summary>
    private static ModelReading<List<string>> ReferencesOf(string projectPath, string solutionDirectory)
    {
        string projectDirectory = Path.GetDirectoryName(projectPath)!;
        ReferenceScan scan = new(projectDirectory, solutionDirectory);

        scan.Visit(projectPath, true);

        for (DirectoryInfo? directory = new(projectDirectory); directory is not null; directory = directory.Parent)
            foreach (string name in DirectoryBuildFileNames)
            {
                string candidate = Path.Combine(directory.FullName, name);
                if (File.Exists(candidate)) scan.Visit(candidate, true);
            }

        return scan.Resolve();
    }

    /// <summary>Determines whether <paramref name="value" /> still needs MSBuild to evaluate it.</summary>
    private static bool IsUnevaluated(string value)
    {
        return UnevaluatedMarkers.Any(marker => value.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>Determines whether an import path that still needs evaluating can declare no reference the closure needs.</summary>
    private static bool IsOutOfReach(string candidate, bool declaredOnTheWalk)
    {
        bool outOfTree = OutOfTreeRoots.Any(root => candidate.StartsWith($"$({root})", StringComparison.OrdinalIgnoreCase));
        return outOfTree || (declaredOnTheWalk && DirectoryBuildLookup().IsMatch(candidate));
    }

    [GeneratedRegex(@"\$\((?<name>[A-Za-z_][A-Za-z0-9_\-]*)\)")]
    private static partial Regex PropertyReference();

    [GeneratedRegex("""^\$\(\[MSBuild\]::GetPathOfFileAbove\(\s*'?Directory\.Build\.(props|targets)'?\s*[,)]""", RegexOptions.IgnoreCase)]
    private static partial Regex DirectoryBuildLookup();

    /// <summary>
    ///     Collects what one project's files declare: their properties, references and imports. Imports are
    ///     followed once every file the walk names has been read, so a property is known wherever it is defined.
    /// </summary>
    private sealed class ReferenceScan
    {
        private readonly List<DeclaredImport> _imports = [];
        private readonly string _projectDirectory;
        private readonly Dictionary<string, HashSet<string>> _properties = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<DeclaredReference> _references = [];
        private readonly HashSet<string> _visited = new(SolutionModel.PathComparer);

        private string? _declined;

        public ReferenceScan(string projectDirectory, string solutionDirectory)
        {
            _projectDirectory = projectDirectory;
            AddProperty("MSBuildProjectDirectory", projectDirectory);
            AddProperty("SolutionDir", Path.TrimEndingDirectorySeparator(solutionDirectory) + Path.DirectorySeparatorChar);
        }

        /// <summary>
        ///     Reads <paramref name="file" />'s properties, references and imports, once. <paramref name="onTheWalk" />
        ///     is true for the project and the <c>Directory.Build</c> files above it.
        /// </summary>
        public void Visit(string file, bool onTheWalk)
        {
            if (_declined is not null || !_visited.Add(file)) return;

            XDocument document;
            try
            {
                using FileStream stream = File.OpenRead(file);
                document = XDocument.Load(stream);
            }
            catch (Exception exception) when (FilesystemFailure.Covers(exception) || exception is XmlException)
            {
                _declined = $"{file} could not be read ({exception.Message})";
                return;
            }

            string fileDirectory = Path.GetDirectoryName(file)!;

            // A ProjectReference with no Include is an Update or a Remove. Neither names a new reference, and a
            // Remove left unapplied only keeps a superset of what is needed.
            foreach (XElement element in document.Descendants())
                if (element.Parent?.Name.LocalName == "PropertyGroup")
                    AddProperty(element.Name.LocalName, Localize(element.Value, fileDirectory));
                else if (element.Name.LocalName == "ProjectReference" && element.Attribute("Include")?.Value is { } include)
                    _references.Add(new DeclaredReference(include, file, fileDirectory));
                else if (element.Name.LocalName == "Import" && element.Attribute("Sdk") is null && element.Attribute("Project")?.Value is { } project)
                    _imports.Add(new DeclaredImport(project, file, fileDirectory, onTheWalk));
        }

        /// <summary>
        ///     Follows every import the files read so far lead to, then returns the absolute path of every reference
        ///     they declare, or why one of them could not be read.
        /// </summary>
        public ModelReading<List<string>> Resolve()
        {
            FollowImports();
            _declined ??= UnplaceableImport();
            if (_declined is not null) return ModelReading<List<string>>.Decline(_declined);

            List<string> references = [];
            foreach (DeclaredReference reference in _references)
            foreach (string entry in reference.Include.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                IEnumerable<string> paths = Expand(entry, reference.FileDirectory)
                    .SelectMany(candidate => candidate.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

                foreach (string path in paths)
                {
                    if (IsUnevaluated(path))
                        return ModelReading<List<string>>.Decline(
                            $"{reference.File} references a project as \"{entry}\", which needs MSBuild to evaluate");

                    references.Add(SolutionModel.ResolveSpelling(path, _projectDirectory));
                }
            }

            return ModelReading<List<string>>.Of(references);
        }

        /// <summary>
        ///     Visits every file an import resolves to, until a pass reads nothing new: a file read for the first
        ///     time can define the property another import was waiting on.
        /// </summary>
        private void FollowImports()
        {
            bool readAny;
            do
            {
                readAny = false;
                foreach (DeclaredImport import in _imports.ToList())
                foreach (string candidate in Expand(import.Project, import.FileDirectory))
                {
                    if (IsUnevaluated(candidate)) continue;

                    string resolved = SolutionModel.ResolveSpelling(candidate, import.FileDirectory);
                    if (_visited.Contains(resolved) || !File.Exists(resolved)) continue;

                    Visit(resolved, false);
                    if (_declined is not null) return;

                    readAny = true;
                }
            } while (readAny);
        }

        /// <summary>Returns why an import that could still declare a reference was not followed, if one was not.</summary>
        private string? UnplaceableImport()
        {
            foreach (DeclaredImport import in _imports)
                if (Expand(import.Project, import.FileDirectory).Any(candidate => IsUnevaluated(candidate) && !IsOutOfReach(candidate, import.OnTheWalk)))
                    return $"{import.File} imports \"{import.Project}\", which needs MSBuild to evaluate";

            return null;
        }

        /// <summary>
        ///     Returns every spelling <paramref name="value" /> takes once the properties the files define are
        ///     substituted, or <paramref name="value" /> with only <see cref="Localize" /> applied when that does not
        ///     settle within <see cref="MaxSubstitutions" /> and <see cref="MaxCandidates" />.
        ///     <paramref name="fileDirectory" /> is the directory of the file that writes it.
        /// </summary>
        private List<string> Expand(string value, string fileDirectory)
        {
            string localized = Localize(value, fileDirectory);
            List<string> candidates = [localized];
            for (var substitution = 0; substitution < MaxSubstitutions; substitution++)
            {
                List<string> next = [];
                var substituted = false;
                foreach (string candidate in candidates)
                {
                    Match? known = PropertyReference().Matches(candidate)
                        .FirstOrDefault(match => _properties.ContainsKey(match.Groups["name"].Value));

                    if (known is null)
                    {
                        next.Add(candidate);
                        continue;
                    }

                    substituted = true;
                    string before = candidate[..known.Index];
                    string after = candidate[(known.Index + known.Length)..];
                    next.AddRange(_properties[known.Groups["name"].Value].Select(propertyValue => before + propertyValue + after));
                }

                if (!substituted) return candidates;

                candidates = next.Distinct(StringComparer.Ordinal).ToList();
                if (candidates.Count > MaxCandidates) return [localized];
            }

            return [localized];
        }

        private void AddProperty(string name, string value)
        {
            if (!_properties.TryGetValue(name, out HashSet<string>? values))
                _properties[name] = values = new HashSet<string>(StringComparer.Ordinal);

            values.Add(value);
        }

        /// <summary>
        ///     Substitutes <c>$(MSBuildThisFileDirectory)</c>, the one property whose value depends on the file that
        ///     names it.
        /// </summary>
        private static string Localize(string value, string fileDirectory)
        {
            return value.Replace("$(MSBuildThisFileDirectory)", fileDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A <c>ProjectReference</c>'s <c>Include</c>, as written, and the file that declares it.</summary>
        private sealed record DeclaredReference(string Include, string File, string FileDirectory);

        /// <summary>An <c>Import</c>'s <c>Project</c>, as written, and the file that declares it.</summary>
        private sealed record DeclaredImport(string Project, string File, string FileDirectory, bool OnTheWalk);
    }
}