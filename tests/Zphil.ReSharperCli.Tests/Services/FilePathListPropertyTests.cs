using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Shouldly;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;

namespace Zphil.ReSharperCli.Tests.Services;

/// <summary>
///     <see cref="FilePathList.Split" /> does not build a new list until it meets the first entry that needs
///     splitting, and then has to graft the entries it already walked past onto the front. That copy-on-first-
///     split is an optimisation over an obvious model — expand every entry, concatenate — and this states
///     that the two agree for any arrangement of entries, which is the class of bug an example test picks up
///     only if someone guessed the right position for the first split.
/// </summary>
/// <remarks>
///     Every generated fragment is relative and the solution directory is a freshly created empty one, so
///     nothing an entry names can exist. That keeps the existing-file guard — which correctly keeps a real
///     <c>Foo,Bar.cs</c> verbatim — out of the comparison, because it is a fact about the disk rather than
///     about the splitting rule under test.
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
}