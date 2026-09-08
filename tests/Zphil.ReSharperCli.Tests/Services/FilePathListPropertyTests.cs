using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Shouldly;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Services;

/// <summary>
///     The two <see cref="FilePathList" /> members whose contract a table of examples cannot state.
///     <see cref="FilePathList.Split" /> does not build a new list until it meets the first entry that needs
///     splitting, and then has to graft the entries it already walked past onto the front — an optimisation
///     over an obvious model (expand every entry, concatenate) which has to agree with it for any
///     arrangement, and that is the class of bug an example test picks up only if someone guessed the right
///     position for the first split. <see cref="FilePathList.ToIncludePattern" /> has to be total, because it
///     runs on the way to <c>jb</c>, past the validation that would otherwise have named a bad entry: every
///     string a path API can refuse must come back as the entry rather than as an exception, and which
///     strings those are is what a generator finds and a reader does not.
/// </summary>
/// <remarks>
///     Every fragment the splitting generators produce is relative, and the solution directory is a freshly
///     created empty one, so nothing an entry names can exist. That keeps the existing-file guard — which
///     correctly keeps a real <c>Foo,Bar.cs</c> verbatim — out of the comparison, because it is a fact about
///     the disk rather than about the splitting rule under test. The totality generator is free of that
///     constraint: it names nothing that could exist either, and what it draws is aimed at the path APIs.
/// </remarks>
public sealed class FilePathListPropertyTests : IDisposable
{
    private readonly FakeEnvironment _environment = new();
    private readonly string _solutionDirectory;

    public FilePathListPropertyTests()
    {
        _solutionDirectory = _environment.CurrentDirectory;
    }

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Property]
    public Property Split_AnyEntryArrangement_MatchesTheNaiveFragmentExpansion()
    {
        return Prop.ForAll(
            EntryList().ToArbitrary(),
            files =>
            {
                // Act
                IReadOnlyList<string> split = FilePathList.Split(files, _solutionDirectory);

                // Assert
                split.ShouldBe(
                    NaiveExpansion(files),
                    $"Splitting [{string.Join(" | ", files)}] must agree with expanding every entry and "
                    + "concatenating. Where they differ, the copy-on-first-split has lost or duplicated an "
                    + "entry it walked past before the first split.");
            });
    }

    [Property]
    public Property Split_AnAlreadySplitList_IsReturnedAsItIs()
    {
        return Prop.ForAll(
            EntryList().ToArbitrary(),
            files =>
            {
                // Arrange
                IReadOnlyList<string> split = FilePathList.Split(files, _solutionDirectory);

                // Act
                IReadOnlyList<string> resplit = FilePathList.Split(split, _solutionDirectory);

                // Assert — the same list, not merely an equal one. "Returns files itself when nothing needed
                // splitting" is the documented signal that a second pass changed nothing, and a caller that
                // normalizes twice must not pay a copy for the second.
                resplit.ShouldBeSameAs(
                    split,
                    $"[{string.Join(" | ", files)}] split to [{string.Join(" | ", split)}], in which no entry "
                    + "carries a delimiter left to act on, so splitting that must hand back the very list.");
            });
    }

    [Property]
    public Property ToIncludePattern_AnyEntryIncludingOnesThePathApisReject_NeverThrows()
    {
        return Prop.ForAll(
            HostileEntry().ToArbitrary(),
            entry =>
            {
                // Act & Assert — translation runs on the way to jb, after validation has decided the call is
                // worth making, so an entry the path APIs refuse has to be left for the error that names it
                // rather than crash a run the caller is already waiting on.
                Should.NotThrow(
                    () => FilePathList.ToIncludePattern(entry, _solutionDirectory),
                    $"An entry of {entry.Length} characters, \"{HostileStrings.Excerpt(entry)}\", must translate or be kept "
                    + "verbatim, never throw.");
            });
    }

    /// <summary>
    ///     The obvious implementation, written for clarity rather than for the allocation the real one avoids:
    ///     expand each entry independently, concatenate the results.
    /// </summary>
    private static IReadOnlyList<string> NaiveExpansion(IReadOnlyList<string> files)
    {
        List<string> expanded = [];
        foreach (string entry in files) expanded.AddRange(ExpandEntry(entry));

        return expanded;
    }

    private static IReadOnlyList<string> ExpandEntry(string entry)
    {
        if (entry.IndexOfAny([';', ',']) < 0) return [entry];

        List<string> fragments = entry.Split(';', ',')
            .Select(fragment => fragment.Trim())
            .Where(fragment => fragment.Length > 0)
            .ToList();

        return fragments.Count > 0 ? fragments : [entry];
    }

    /// <summary>
    ///     A <c>files</c> argument: a short list of entries, each either a lone path or several joined the way
    ///     the mistake this rescues actually arrives. The awkward arrangements — an entry that is nothing but
    ///     delimiters, empty fragments between two real ones, leading and trailing delimiters — are unioned in
    ///     rather than left for a lucky draw, because they are where the "keep it verbatim" fallbacks live.
    /// </summary>
    private static Gen<IReadOnlyList<string>> EntryList()
    {
        return Gen.Choose(1, 5)
            .SelectMany(count => Entry().ListOf(count))
            .Select(entries => (IReadOnlyList<string>)entries.ToList());
    }

    private static Gen<string> Entry()
    {
        Gen<List<string>> fragments = Gen.Choose(1, 4)
            .SelectMany(count => Fragment().ListOf(count));

        Gen<string> joined = fragments.SelectMany(
            _ => Gen.Elements(",", ";", ", ", " ; ", ",,"),
            (parts, separator) => string.Join(separator, parts));

        Gen<string> awkward = Fragment().SelectMany(
            _ => Gen.Elements(",{0}", "{0},", ",", ";", ",;,", " , ", "{0}, ,{0}"),
            (fragment, shape) => string.Format(shape, fragment));

        return Gen.OneOf(Fragment(), joined, awkward);
    }

    /// <summary>
    ///     A relative path naming a file that cannot exist under a fresh temp directory. No <c>..</c> segment
    ///     is ever generated, so no entry can resolve outside that directory and accidentally find something.
    /// </summary>
    private static Gen<string> Fragment()
    {
        Gen<List<string>> directories = Gen.Choose(0, 2)
            .SelectMany(directoryDepth => Name().ListOf(directoryDepth));

        return directories
            .SelectMany(_ => Name(), (path, name) => (Path: path, Name: name))
            .SelectMany(
                _ => Gen.Elements(".cs", ".razor", ""),
                (fragment, extension) =>
                    string.Concat(fragment.Path.Select(directory => directory + "/"))
                    + fragment.Name
                    + extension);
    }

    private static Gen<string> Name()
    {
        return Gen.Choose(1, 6)
            .SelectMany(length => Gen.Elements("abZ09_-".ToCharArray()).ListOf(length))
            .Select(characters => new string(characters.ToArray()));
    }

    /// <summary>
    ///     Anything a <c>files</c> entry can arrive as, aimed at the path APIs rather than at <c>jb</c>:
    ///     arbitrary strings unioned with the shapes those APIs refuse or treat specially — an embedded null,
    ///     a lone surrogate, a bare device or UNC prefix, a drive-relative entry, a stream-qualified name, and
    ///     lengths on both sides of the operating system's path limit. No UNC host name is drawn: resolving
    ///     one would put a network round trip inside a property that runs a hundred times.
    /// </summary>
    private Gen<string> HostileEntry()
    {
        var separator = Path.DirectorySeparatorChar.ToString();
        string[] corpus =
        [
            "",
            " ",
            "\t\r\n",
            "src/\0.cs",
            _solutionDirectory + separator + "\0.cs",
            "\ud800",
            "src/\u0001\u001f.cs",
            "C:",
            "C:foo",
            "/src/x",
            @"\\",
            @"\\?\",
            @"\\?\C:\x",
            @"\\.\C:\x",
            $"..{separator}..{separator}..{separator}A.cs",
            @"C:\foo:bar",
            new string('s', 300) + ".cs",
            $"~{separator}A.cs",
            new('a', 40_000),
            _solutionDirectory + separator + new string('a', 40_000)
        ];

        return HostileStrings.AnyOr(corpus);
    }
}