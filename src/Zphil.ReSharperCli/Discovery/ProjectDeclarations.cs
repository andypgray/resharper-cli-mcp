namespace Zphil.ReSharperCli.Discovery;

/// <summary>
///     Represents what one project's files declare, read from the XML rather than evaluated: the projects it
///     references, its <c>Compile</c> items, the switches that turn its default items off, and whether it is an SDK
///     project.
/// </summary>
/// <remarks>
///     The two item readings can decline on their own, because each reader needs only one of them: the reference
///     closure never looks at a <c>Compile</c> item, and the owner proof never follows a reference.
/// </remarks>
/// <param name="References">The absolute path of every project it references, or why one needs MSBuild.</param>
/// <param name="CompileItems">Every <c>Compile</c> item its files declare, or why one needs MSBuild.</param>
/// <param name="DefaultItemSwitches">
///     Every value <c>EnableDefaultCompileItems</c> or <c>EnableDefaultItems</c> is assigned, as written and
///     trimmed, conditions ignored.
/// </param>
/// <param name="IsSdkStyle">Whether the project file itself names an SDK.</param>
/// <param name="UnreadImport">
///     Why an import from the package folder was left unread, when one was: the closure needs nothing from it, but
///     it could declare a <c>Compile</c> item.
/// </param>
internal sealed record ProjectDeclarations(
    ModelReading<IReadOnlyList<string>> References,
    ModelReading<IReadOnlyList<CompileItem>> CompileItems,
    IReadOnlyList<DefaultItemSwitch> DefaultItemSwitches,
    bool IsSdkStyle,
    string? UnreadImport);

/// <summary>Defines what a <c>Compile</c> element does to the project's list of compiled files.</summary>
internal enum CompileOperation
{
    /// <summary>Adds files.</summary>
    Include,

    /// <summary>Takes files out.</summary>
    Remove,

    /// <summary>Changes the metadata of files already in.</summary>
    Update
}

/// <summary>
///     Represents one <c>Compile</c> element: what it does, the value as written, and the absolute paths that value
///     expands to.
/// </summary>
/// <remarks>
///     A path may still hold a wildcard, since one is ordinary in an item and only <c>jb</c> or MSBuild expands it.
///     Relative paths resolve against the project's directory, as MSBuild resolves an item wherever it is declared.
/// </remarks>
internal sealed record CompileItem(CompileOperation Operation, string Spelling, IReadOnlyList<string> Paths);

/// <summary>Represents one value given to a property that turns default items on or off.</summary>
internal sealed record DefaultItemSwitch(string Property, string Value);

/// <summary>
///     Reads each project's <see cref="ProjectDeclarations" /> once, so every reader in one decision shares the parse.
/// </summary>
/// <remarks>Not thread-safe; one instance serves one decision.</remarks>
internal sealed class ProjectReadings(string solutionDirectory)
{
    private readonly Dictionary<string, ModelReading<ProjectDeclarations>> _readings = new(SolutionModel.PathComparer);

    /// <summary>
    ///     Returns what <paramref name="projectPath" /> declares, or why its files could not be read.
    /// </summary>
    public ModelReading<ProjectDeclarations> Of(string projectPath)
    {
        if (!_readings.TryGetValue(projectPath, out ModelReading<ProjectDeclarations>? reading))
            _readings[projectPath] = reading = ProjectGraph.Read(projectPath, solutionDirectory);

        return reading;
    }
}