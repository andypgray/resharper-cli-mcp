namespace Zphil.ReSharperCli.Discovery;

/// <summary>
///     Proves that the project a file's directory implies is the only one of the solution that compiles it, read
///     from what each project declares rather than evaluated.
/// </summary>
/// <remarks>
///     <para>
///         A cleanup loaded with fewer projects than the solution rewrites a file in the compilation contexts it
///         loaded. When another project also compiles the file, through a link, a shared project, a glob or a
///         default glob from a directory above, a context is missing and the result can differ from a
///         solution-wide pass; when the owner itself removes or updates the file, the owner may not compile it at
///         all. So anything short of proof declines, and the caller runs solution-wide.
///     </para>
///     <para>
///         Whether an item could name the file is a superset: a value with no wildcard names the one file it
///         resolves to, and one with a wildcard names every file under the directory its wildcard-free prefix
///         spells. <c>Exclude</c> is ignored for the same reason.
///     </para>
///     <para>
///         Only <c>Compile</c> items are read, and an SDK project's default <c>Compile</c> glob names its own
///         language's source files, so only a <c>.cs</c> file under a <c>.csproj</c> or a <c>.vb</c> file under a
///         <c>.vbproj</c> can be proved. Any other file can reach compilation through items this proof does not
///         read, a <c>.razor</c> file through <c>Content</c> and <c>RazorComponent</c> or a <c>.xaml</c> file
///         through <c>Page</c>, so it declines.
///     </para>
/// </remarks>
internal static class CompileOwnership
{
    private static readonly Dictionary<string, string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".csproj"] = ".cs",
        [".vbproj"] = ".vb"
    };

    private static readonly char[] Wildcards = ['*', '?'];

    /// <summary>
    ///     Returns <paramref name="owner" /> when it is provably the only project of <paramref name="solution" /> that
    ///     compiles <paramref name="file" />, an absolute path, or why that could not be proved.
    /// </summary>
    public static ModelReading<SolutionProject> Prove(
        IReadOnlyList<SolutionProject> solution,
        SolutionProject owner,
        string file,
        ProjectReadings readings)
    {
        string projectExtension = Path.GetExtension(owner.FullPath);
        if (!SourceExtensions.TryGetValue(projectExtension, out string? sourceExtension))
            return Decline($"{owner.Spelling} is not a C# or Visual Basic project, whose default Compile items are the only ones read");

        if (!Path.GetExtension(file).Equals(sourceExtension, StringComparison.OrdinalIgnoreCase))
            return Decline($"only a {sourceExtension} file is compiled through the Compile items of {owner.Spelling}, the only items read to prove its owner");

        ModelReading<ProjectDeclarations> ownerReading = readings.Of(owner.FullPath);
        if (ownerReading.Value is not { } declarations) return Decline(ownerReading.Declined!);

        if (!declarations.IsSdkStyle)
            return Decline($"{owner.Spelling} is not an SDK project, so only its own item list says what it compiles");

        if (declarations.DefaultItemSwitches.FirstOrDefault(entry => !entry.Value.Equals("true", StringComparison.OrdinalIgnoreCase)) is { } off)
            return Decline($"{owner.Spelling} sets {off.Property} to \"{off.Value}\", so its directory may not say what it compiles");

        string ownerDirectory = Path.GetDirectoryName(owner.FullPath)!;
        foreach (SolutionProject project in solution)
        {
            ModelReading<ProjectDeclarations> reading = readings.Of(project.FullPath);
            if (reading.Value is null) return Decline(reading.Declined!);

            if (reading.Value.UnreadImport is { } unread) return Decline(unread);

            ModelReading<IReadOnlyList<CompileItem>> items = reading.Value.CompileItems;
            if (items.Value is null) return Decline(items.Declined!);

            bool isOwner = SolutionModel.PathComparer.Equals(project.FullPath, owner.FullPath);
            if (FirstNaming(items.Value, file, isOwner ? [CompileOperation.Remove, CompileOperation.Update] : [CompileOperation.Include]) is { } item)
                return Decline(
                    isOwner
                        ? $"{owner.Spelling} may not compile it as its directory implies, through Compile {item.Operation} \"{item.Spelling}\""
                        : $"{project.Spelling} may compile it too, through Compile Include \"{item.Spelling}\"");

            if (!isOwner && Contains(Path.GetDirectoryName(project.FullPath)!, ownerDirectory))
                return Decline($"{project.Spelling} is in a directory above {owner.Spelling}, so its default items may compile it too");
        }

        return ModelReading<SolutionProject>.Of(owner);
    }

    private static ModelReading<SolutionProject> Decline(string reason)
    {
        return ModelReading<SolutionProject>.Decline(reason);
    }

    /// <summary>
    ///     Returns the first of <paramref name="items" /> doing one of <paramref name="operations" /> that could name
    ///     <paramref name="file" />.
    /// </summary>
    private static CompileItem? FirstNaming(IEnumerable<CompileItem> items, string file, CompileOperation[] operations)
    {
        return items.FirstOrDefault(item => operations.Contains(item.Operation) && item.Paths.Any(path => CouldName(path, file)));
    }

    /// <summary>
    ///     Determines whether the item path <paramref name="pattern" /> could name <paramref name="file" />: exactly,
    ///     or for a wildcard, by <paramref name="file" /> being under the directory the pattern spells before it.
    /// </summary>
    private static bool CouldName(string pattern, string file)
    {
        int wildcard = pattern.IndexOfAny(Wildcards);
        if (wildcard < 0) return SolutionModel.PathComparer.Equals(pattern, file);

        int separator = pattern.LastIndexOf(Path.DirectorySeparatorChar, wildcard);
        return separator < 0 || Contains(pattern[..separator], file);
    }

    /// <summary>Determines whether <paramref name="path" /> is strictly under the directory <paramref name="directory" />.</summary>
    private static bool Contains(string directory, string path)
    {
        string prefix = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length && SolutionModel.PathComparer.Equals(path[..prefix.Length], prefix);
    }
}