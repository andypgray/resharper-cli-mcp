using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Discovery;

/// <summary>
///     Reads what a project's files declare (<see cref="Read" />), and from that which of a solution's projects a set
///     of projects needs loaded beside it: the transitive closure of their <c>ProjectReference</c> items.
/// </summary>
/// <remarks>
///     <para>
///         Read from the XML, not evaluated. Conditions are ignored, so a reference that only some configuration
///         takes is followed anyway: a project loaded needlessly costs a little time, while one left out is
///         resolved by <c>jb</c> from its build output, which a stale build makes wrong. A reference to a
///         project outside the solution is skipped, because a solution-wide run would not load it either.
///     </para>
///     <para>
///         A project's items can be declared outside its own file, so a reading also covers every
///         <c>Directory.Build.props</c>, <c>Directory.Build.targets</c> and <c>Directory.Packages.props</c> on the
///         project's ancestor chain (all of them, a superset of the nearest one MSBuild imports), the project's
///         <c>.user</c> file, every file one of those or the project imports, and the file each of the SDK's import
///         hooks (<see cref="ImportHookProperties" />) names. A relative item path found in any of them resolves
///         against the project's directory, as MSBuild resolves an item; an import's path resolves against the file
///         that imports it, and a hook's has to be rooted, since the SDK resolves it from its own directory. MSBuild
///         escapes (<c>%</c> and two hex digits) in an item or import path are decoded.
///     </para>
///     <para>
///         A path may use <c>$(MSBuildThisFileDirectory)</c>, <c>$(MSBuildProjectDirectory)</c>,
///         <c>$(SolutionDir)</c>, and any property a <c>PropertyGroup</c> in the files read defines. Every value a
///         property is given anywhere counts, and so does leaving it unset wherever an assignment may not apply: a
///         condition on the assignment, its group, an enclosing <c>Choose</c>, or the import that brought its file
///         in, an import that expands to more than one file, and an ancestor file farther up than the nearest of its
///         name. So a path that names a property
///         expands to each spelling it can take, and the order in which the files define and use a property does
///         not matter.
///     </para>
///     <para>
///         An import whose path still needs evaluating is not followed when it starts at a package or toolset
///         folder (<see cref="PackageRoot" />, <see cref="ToolsetRoots" />), or when a file the ancestor walk reads
///         uses it to look up a <c>Directory.Build</c> file, which the walk reads anyway. Any other one declines the
///         whole reading, because an item declared inside it could not be seen, and so does a file that will not
///         parse. A <c>ProjectReference</c> or <c>Compile</c> item whose value still needs a property or an item
///         list declines that item reading alone, and so does a wildcard in a reference.
///     </para>
/// </remarks>
internal static partial class ProjectGraph
{
    /// <summary>How many property references one path may expand through before it counts as unevaluated.</summary>
    /// <remarks>Bounds a property defined in terms of itself, which would otherwise expand forever.</remarks>
    private const int MaxSubstitutions = 32;

    /// <summary>How many distinct paths one spelling may expand to before it counts as unevaluated.</summary>
    private const int MaxCandidates = 64;

    /// <summary>The property naming the NuGet package folder, which holds none of a solution's projects.</summary>
    /// <remarks>
    ///     An import from it declares no reference the closure needs, but could still declare a <c>Compile</c>
    ///     item, so <see cref="ProjectDeclarations.UnreadImport" /> names it.
    /// </remarks>
    private const string PackageRoot = "NuGetPackageRoot";

    /// <summary>The files the SDK imports from the nearest directory above a project that holds one, by name.</summary>
    private static readonly string[] AncestorFileNames = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"];

    /// <summary>
    ///     The properties whose value the SDK imports as a file, from its own targets and without an <c>Import</c>
    ///     element in the project's files, so each is read as an import of its value.
    /// </summary>
    private static readonly string[] ImportHookProperties =
    [
        "DirectoryBuildPropsPath",
        "DirectoryBuildTargetsPath",
        "CustomBeforeMicrosoftCommonProps",
        "CustomAfterMicrosoftCommonProps",
        "CustomBeforeMicrosoftCommonTargets",
        "CustomAfterMicrosoftCommonTargets",
        "CustomBeforeMicrosoftCSharpTargets",
        "CustomAfterMicrosoftCSharpTargets",
        "BeforeMicrosoftNETSdkTargets",
        "AfterMicrosoftNETSdkTargets"
    ];

    /// <summary>What marks a value as one only MSBuild can finish expanding.</summary>
    private static readonly string[] EvaluationMarkers = ["$(", "@(", "%("];

    private static readonly char[] Wildcards = ['*', '?'];

    /// <summary>The properties that turn an SDK project's default items off.</summary>
    private static readonly string[] DefaultItemProperties = ["EnableDefaultCompileItems", "EnableDefaultItems"];

    /// <summary>
    ///     The properties that name the MSBuild toolset, whose files are the SDK's own and know nothing of the
    ///     solution's tree, so an import under one declares no item either reader needs.
    /// </summary>
    private static readonly string[] ToolsetRoots =
    [
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
    /// </summary>
    public static ModelReading<IReadOnlyList<SolutionProject>> Closure(
        IReadOnlyList<SolutionProject> solution,
        IEnumerable<SolutionProject> roots,
        ProjectReadings readings)
    {
        Dictionary<string, SolutionProject> byPath = solution.ToDictionary(project => project.FullPath, SolutionModel.PathComparer);
        HashSet<string> reached = new(SolutionModel.PathComparer);
        Queue<SolutionProject> pending = new();

        foreach (SolutionProject root in roots)
            if (reached.Add(root.FullPath))
                pending.Enqueue(root);

        while (pending.TryDequeue(out SolutionProject? project))
        {
            ModelReading<ProjectDeclarations> declarations = readings.Of(project.FullPath);
            if (declarations.Value is null) return ModelReading<IReadOnlyList<SolutionProject>>.Decline(declarations.Declined!);

            ModelReading<IReadOnlyList<string>> references = declarations.Value.References;
            if (references.Value is null) return ModelReading<IReadOnlyList<SolutionProject>>.Decline(references.Declined!);

            foreach (string reference in references.Value)
                if (byPath.TryGetValue(reference, out SolutionProject? referenced) && reached.Add(reference))
                    pending.Enqueue(referenced);
        }

        List<SolutionProject> closure = solution.Where(project => reached.Contains(project.FullPath)).ToList();
        return ModelReading<IReadOnlyList<SolutionProject>>.Of(closure);
    }

    /// <summary>
    ///     Returns what <paramref name="projectPath" />'s files declare, or why they could not be read.
    ///     <paramref name="solutionDirectory" /> is the directory <c>$(SolutionDir)</c> names.
    /// </summary>
    internal static ModelReading<ProjectDeclarations> Read(string projectPath, string solutionDirectory)
    {
        string projectDirectory = Path.GetDirectoryName(projectPath)!;
        ProjectScan scan = new(projectPath, solutionDirectory);

        scan.Visit(projectPath, true, false);

        // The SDK imports the project's .user file beside it whenever there is one.
        string userFile = projectPath + ".user";
        if (File.Exists(userFile)) scan.Visit(userFile, false, false);

        // MSBuild imports the nearest file of each name, so any farther one is read as one it may not import.
        HashSet<string> nearestFound = new(StringComparer.OrdinalIgnoreCase);
        for (DirectoryInfo? directory = new(projectDirectory); directory is not null; directory = directory.Parent)
            foreach (string name in AncestorFileNames)
            {
                string candidate = Path.Combine(directory.FullName, name);
                if (File.Exists(candidate)) scan.Visit(candidate, true, !nearestFound.Add(name));
            }

        return scan.Resolve();
    }

    /// <summary>Determines whether <paramref name="value" /> still needs MSBuild to evaluate it.</summary>
    private static bool NeedsEvaluating(string value)
    {
        return EvaluationMarkers.Any(marker => value.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>
    ///     Determines whether <paramref name="candidate" /> is a path under a property naming one of
    ///     <paramref name="roots" />.
    /// </summary>
    private static bool StartsAtAny(string candidate, IEnumerable<string> roots)
    {
        return roots.Any(root => candidate.StartsWith($"$({root})", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Determines whether <paramref name="element" /> applies only under some condition: its own, an enclosing
    ///     element's, or an <c>Otherwise</c> branch's.
    /// </summary>
    private static bool IsConditional(XElement element)
    {
        return element.AncestorsAndSelf().Any(ancestor => ancestor.Attribute("Condition") is not null || ancestor.Name.LocalName == "Otherwise");
    }

    /// <summary>Replaces each MSBuild escape (<c>%</c> and two hex digits) with the character it stands for.</summary>
    private static string Unescape(string value)
    {
        return MSBuildEscape().Replace(value, match => ((char)Convert.ToInt32(match.Groups["hex"].Value, 16)).ToString());
    }

    [GeneratedRegex(@"\$\((?<name>[A-Za-z_][A-Za-z0-9_\-]*)\)")]
    private static partial Regex PropertyReference();

    [GeneratedRegex("%(?<hex>[0-9A-Fa-f]{2})")]
    private static partial Regex MSBuildEscape();

    [GeneratedRegex("""^\$\(\[MSBuild\]::GetPathOfFileAbove\(\s*'?Directory\.Build\.(props|targets)'?\s*[,)]""", RegexOptions.IgnoreCase)]
    private static partial Regex DirectoryBuildLookup();

    /// <summary>
    ///     Collects what one project's files declare: their properties, references, compile items and imports.
    ///     Imports are followed once every file the walk names has been read, so a property is known wherever it
    ///     is defined.
    /// </summary>
    private sealed class ProjectScan
    {
        private readonly List<(CompileOperation Operation, DeclaredItem Item)> _compileItems = [];
        private readonly List<DeclaredImport> _imports = [];
        private readonly string _projectDirectory;
        private readonly string _projectPath;
        private readonly Dictionary<string, HashSet<string>> _properties = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<DeclaredItem> _references = [];
        private readonly List<DefaultItemSwitch> _switches = [];
        private readonly HashSet<string> _visited = new(SolutionModel.PathComparer);

        private string? _declined;
        private bool _isSdkStyle;

        public ProjectScan(string projectPath, string solutionDirectory)
        {
            _projectPath = projectPath;
            _projectDirectory = Path.GetDirectoryName(projectPath)!;
            AddProperty("MSBuildProjectDirectory", _projectDirectory, false);
            AddProperty("SolutionDir", Path.TrimEndingDirectorySeparator(solutionDirectory) + Path.DirectorySeparatorChar, false);
        }

        /// <summary>
        ///     Reads <paramref name="file" />'s properties, items and imports, once. <paramref name="onTheWalk" /> is
        ///     true for the project and the <c>Directory.Build</c> files above it; <paramref name="conditional" /> for
        ///     a file MSBuild may not import.
        /// </summary>
        public void Visit(string file, bool onTheWalk, bool conditional)
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

            if (SolutionModel.PathComparer.Equals(file, _projectPath)) _isSdkStyle = NamesAnSdk(document);

            // A ProjectReference with no Include is an Update or a Remove. Neither names a new reference, and a
            // Remove left unapplied only keeps a superset of what is needed.
            foreach (XElement element in document.Descendants())
                if (element.Parent?.Name.LocalName == "PropertyGroup")
                    VisitProperty(element, file, fileDirectory, onTheWalk, conditional || IsConditional(element));
                else if (element.Name.LocalName == "ProjectReference" && element.Attribute("Include")?.Value is { } include)
                    _references.Add(new DeclaredItem(include, file, fileDirectory));
                else if (element.Name.LocalName == "Compile" && CompileItemOf(element, file, fileDirectory) is { } compile)
                    _compileItems.Add(compile);
                else if (element.Name.LocalName == "Import" && element.Attribute("Sdk") is null && element.Attribute("Project")?.Value is { } project)
                    _imports.Add(new DeclaredImport(project, file, fileDirectory, onTheWalk, conditional || IsConditional(element), null));
        }

        /// <summary>
        ///     Records one property assignment: its value, the raw value when it switches default items, and an
        ///     import of its value when the SDK imports what it names.
        /// </summary>
        private void VisitProperty(XElement element, string file, string fileDirectory, bool onTheWalk, bool conditional)
        {
            string name = element.Name.LocalName;
            AddProperty(name, Localize(element.Value, fileDirectory), conditional);

            if (DefaultItemProperties.Contains(name, StringComparer.OrdinalIgnoreCase))
                _switches.Add(new DefaultItemSwitch(name, element.Value.Trim()));

            if (ImportHookProperties.Contains(name, StringComparer.OrdinalIgnoreCase) && element.Value.Trim().Length > 0)
                _imports.Add(new DeclaredImport(element.Value.Trim(), file, fileDirectory, onTheWalk, conditional, name));
        }

        /// <summary>
        ///     Follows every import the files read so far lead to, then returns what they declare, or why one of them
        ///     could not be read.
        /// </summary>
        public ModelReading<ProjectDeclarations> Resolve()
        {
            FollowImports();
            _declined ??= UnplaceableImport();
            if (_declined is not null) return ModelReading<ProjectDeclarations>.Decline(_declined);

            ProjectDeclarations declarations = new(ResolveReferences(), ResolveCompileItems(), _switches, _isSdkStyle, UnreadPackageImport());
            return ModelReading<ProjectDeclarations>.Of(declarations);
        }

        /// <summary>Determines whether a project file names an SDK, by any of the three spellings MSBuild accepts.</summary>
        private static bool NamesAnSdk(XDocument document)
        {
            if (document.Root is not { } root) return false;

            return root.Attribute("Sdk") is not null
                   || root.Elements().Any(element => element.Name.LocalName == "Sdk"
                                                     || (element.Name.LocalName == "Import" && element.Attribute("Sdk") is not null));
        }

        private static (CompileOperation, DeclaredItem)? CompileItemOf(XElement element, string file, string fileDirectory)
        {
            foreach (CompileOperation operation in Enum.GetValues<CompileOperation>())
                if (element.Attribute(operation.ToString())?.Value is { } value)
                    return (operation, new DeclaredItem(value, file, fileDirectory));

            return null;
        }

        private ModelReading<IReadOnlyList<string>> ResolveReferences()
        {
            List<string> references = [];
            foreach (DeclaredItem reference in _references)
            foreach ((string entry, string path) in ExpandItem(reference))
            {
                if (NeedsEvaluating(path) || path.IndexOfAny(Wildcards) >= 0)
                    return ModelReading<IReadOnlyList<string>>.Decline(
                        $"{reference.File} references a project as \"{entry}\", which needs MSBuild to evaluate");

                references.Add(SolutionModel.ResolveSpelling(path, _projectDirectory));
            }

            return ModelReading<IReadOnlyList<string>>.Of(references);
        }

        private ModelReading<IReadOnlyList<CompileItem>> ResolveCompileItems()
        {
            List<CompileItem> items = [];
            foreach ((CompileOperation operation, DeclaredItem item) in _compileItems)
            {
                List<string> paths = [];
                foreach ((string entry, string path) in ExpandItem(item))
                {
                    if (NeedsEvaluating(path))
                        return ModelReading<IReadOnlyList<CompileItem>>.Decline(
                            $"{item.File} declares Compile {operation} \"{entry}\", which needs MSBuild to evaluate");

                    paths.Add(SolutionModel.ResolveSpelling(path, _projectDirectory));
                }

                items.Add(new CompileItem(operation, item.Value, paths));
            }

            return ModelReading<IReadOnlyList<CompileItem>>.Of(items);
        }

        /// <summary>
        ///     Returns each <c>;</c>-separated entry of <paramref name="item" />'s value beside every path it expands
        ///     to, itself split on <c>;</c> since a property's value can hold a list.
        /// </summary>
        private IEnumerable<(string Entry, string Path)> ExpandItem(DeclaredItem item)
        {
            foreach (string entry in item.Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            foreach (string candidate in Expand(entry, item.FileDirectory))
            foreach (string path in candidate.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                yield return (entry, Unescape(path));
        }

        /// <summary>
        ///     Visits every file an import resolves to, until a pass reads nothing new: a file read for the first
        ///     time can define the property another import was waiting on.
        /// </summary>
        /// <remarks>
        ///     An import that expands to more than one file imports only one of them, so each is read as a file
        ///     MSBuild may not import.
        /// </remarks>
        private void FollowImports()
        {
            bool readAny;
            do
            {
                readAny = false;
                foreach (DeclaredImport import in _imports.ToList())
                {
                    List<string> placeable = PlaceableSpellings(import).ToList();
                    foreach (string candidate in placeable)
                    {
                        string resolved = SolutionModel.ResolveSpelling(Unescape(candidate), import.FileDirectory);
                        if (_visited.Contains(resolved) || !File.Exists(resolved)) continue;

                        Visit(resolved, false, import.Conditional || placeable.Count > 1);
                        if (_declined is not null) return;

                        readAny = true;
                    }
                }
            } while (readAny);
        }

        /// <summary>
        ///     Returns the spellings of <paramref name="import" /> that name a file this scan can read: fully
        ///     evaluated, and rooted for a hook property, whose value the SDK resolves from its own directory.
        /// </summary>
        private IEnumerable<string> PlaceableSpellings(DeclaredImport import)
        {
            return Expand(import.Project, import.FileDirectory)
                .Where(candidate => !NeedsEvaluating(candidate) && candidate.IndexOfAny(Wildcards) < 0)
                .Where(candidate => import.Hook is null || Path.IsPathRooted(candidate));
        }

        /// <summary>Returns why an import that could still declare an item was not followed, if one was not.</summary>
        private string? UnplaceableImport()
        {
            DeclaredImport? relativeHook = _imports.FirstOrDefault(declared => declared.Hook is not null
                                                                               && Expand(declared.Project, declared.FileDirectory).Any(candidate => candidate.Length > 0
                                                                                                                                                    && !NeedsEvaluating(candidate)
                                                                                                                                                    && !Path.IsPathRooted(candidate)));
            if (relativeHook is not null)
                return $"{relativeHook.File} imports \"{relativeHook.Project}\" through {relativeHook.Hook}, which MSBuild resolves from the SDK's own directory";

            DeclaredImport? import = _imports.FirstOrDefault(declared => UnfollowedSpellings(declared).Any(candidate => !IsOutOfReach(candidate, declared.OnTheWalk)));

            return import is null ? null : $"{import.File} imports \"{import.Project}\", which needs MSBuild to evaluate";
        }

        /// <summary>Returns why an import from the package folder was left unread, if one was.</summary>
        private string? UnreadPackageImport()
        {
            DeclaredImport? import = _imports.FirstOrDefault(declared => UnfollowedSpellings(declared).Any(candidate => StartsAtAny(candidate, [PackageRoot])));

            return import is null ? null : $"{import.File} imports \"{import.Project}\" from the package folder, which is not read";
        }

        /// <summary>Returns the spellings of <paramref name="import" /> that still need evaluating, and so were not followed.</summary>
        private IEnumerable<string> UnfollowedSpellings(DeclaredImport import)
        {
            return Expand(import.Project, import.FileDirectory).Where(candidate => NeedsEvaluating(candidate) || candidate.IndexOfAny(Wildcards) >= 0);
        }

        /// <summary>Determines whether an import path that still needs evaluating can declare nothing the closure needs.</summary>
        private static bool IsOutOfReach(string candidate, bool declaredOnTheWalk)
        {
            bool outOfTree = StartsAtAny(candidate, [PackageRoot, .. ToolsetRoots]);
            return outOfTree || (declaredOnTheWalk && DirectoryBuildLookup().IsMatch(candidate));
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

        /// <summary>
        ///     Records <paramref name="value" /> as one <paramref name="name" /> can take, and, when the assignment is
        ///     <paramref name="conditional" />, the empty value it has where the assignment does not apply.
        /// </summary>
        private void AddProperty(string name, string value, bool conditional)
        {
            if (!_properties.TryGetValue(name, out HashSet<string>? values))
                _properties[name] = values = new HashSet<string>(StringComparer.Ordinal);

            values.Add(value);
            if (conditional) values.Add("");
        }

        /// <summary>
        ///     Substitutes <c>$(MSBuildThisFileDirectory)</c>, the one property whose value depends on the file that
        ///     names it.
        /// </summary>
        private static string Localize(string value, string fileDirectory)
        {
            return value.Replace("$(MSBuildThisFileDirectory)", fileDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>An item's value, as written, and the file that declares it.</summary>
        private sealed record DeclaredItem(string Value, string File, string FileDirectory);

        /// <summary>
        ///     An <c>Import</c>'s <c>Project</c>, as written, the file that declares it, whether MSBuild may skip it,
        ///     and the hook property that names it when there is no <c>Import</c> element.
        /// </summary>
        private sealed record DeclaredImport(string Project, string File, string FileDirectory, bool OnTheWalk, bool Conditional, string? Hook);
    }
}