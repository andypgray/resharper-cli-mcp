using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Shouldly;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     <see cref="JbProgressLines.Classify" /> is fed every line a <c>jb</c> run writes, one at a time,
///     while a caller is waiting on that run. So what it does with a line it was not written for matters as
///     much as what it does with the vocabulary: an exception here comes out of the reader that observes
///     <c>jb</c>'s output in flight, which is not where a caller can act on it. These state the two halves —
///     it survives anything, and it recognises <c>jb</c>'s own lines for any file or profile name rather
///     than for the handful an example table can list.
/// </summary>
/// <remarks>
///     Nothing here says what <c>jb</c> emits — that is the <c>JbContract</c> suite's business, observed
///     against a real run. These say what this reader does with a line once it has one, which is why the
///     generators are free to draw lines <c>jb</c> would never write.
/// </remarks>
public sealed class JbProgressLinesPropertyTests
{
    // Whitespace that does not look like whitespace, spelt by code point so it survives an editor: the
    // no-break space a console can emit, and two of the wide spaces char.IsWhiteSpace also accepts. What
    // Trim removes and what this reader calls blank have to be the same set, and these are where they part.
    private const string NoBreakSpace = "\u00A0";
    private const string EmSpace = "\u2003";
    private const string IdeographicSpace = "\u3000";

    /// <summary>
    ///     Lines that sit on the recogniser's edges, hit on every seed rather than waited for: the
    ///     announcements with junk on either side, the case-shifted announcement, a prefix with nothing
    ///     after it, a truncated cleanup banner, and the bare per-file line <c>cleanupcode</c> writes.
    /// </summary>
    private static readonly string[] KnownLines =
    [
        "",
        " ",
        "\t\r\n",
        NoBreakSpace,
        EmSpace + IdeographicSpace,
        JbProgressLines.AnalyzingPhaseLine,
        "  " + JbProgressLines.AnalyzingPhaseLine + "  ",
        JbProgressLines.AnalyzingPhaseLine + " now",
        "Now " + JbProgressLines.AnalyzingPhaseLine,
        JbProgressLines.InspectingPhaseLine,
        "analyzing files",
        "Analyzing",
        "Analyzing ",
        "Inspecting",
        "Cleaning up using profil",
        JbProgressLines.CleaningPhasePrefix,
        "<ContractFixture>\\Misformatted.cs",
        "src/\0.cs",
        "\ud800",
        new('x', 10_000)
    ];

    /// <summary>Runs of whitespace, every one of which <see cref="string.Trim()" /> is defined to remove.</summary>
    private static readonly string[] WhitespaceRuns =
    [
        "", " ", "  ", "\t", "\r", "\n", "\r\n", NoBreakSpace, EmSpace, IdeographicSpace, " \t "
    ];

    /// <summary>
    ///     File names <c>jb</c> could put after a per-file prefix: nested paths in both separators, spaces,
    ///     dots, generated-file suffixes, non-ASCII — and names that carry the announcement words
    ///     themselves, which is where a recogniser that searched rather than anchored would go wrong.
    /// </summary>
    private static readonly string[] KnownFileNames =
    [
        "Sample.cs",
        "src/Deeply/Nested/File.cs",
        "src\\Deeply\\Nested\\File.razor",
        "ContractFixture.GlobalUsings.g.cs",
        "a file with spaces.cs",
        "Ünicöde.Файл.cs",
        "files.cs",
        "files/Foo.cs",
        "Analyzing files.cs",
        "Running inspections.cs",
        "Cleaning up using profile.cs",
        ".."
    ];

    /// <summary>Profile names, including the empty one a truncated banner would leave.</summary>
    private static readonly string[] KnownProfileNames = ["", " Built-in: Full Cleanup", ": Reformat", "\t"];

    [Property]
    public Property Classify_AnyLine_NeverThrows()
    {
        return Prop.ForAll(
            AnyLine().ToArbitrary(),
            line =>
            {
                // Act & Assert — this runs on the process reader's thread, inside a run the caller is
                // already waiting on, so an unrecognised line has to be answered rather than thrown at.
                Should.NotThrow(
                    () => JbProgressLines.Classify(line),
                    $"A line of {line.Length} characters, \"{HostileStrings.Excerpt(line)}\", must classify or say nothing.");
            });
    }

    [Property]
    public Property Classify_AnyLine_IgnoresSurroundingWhitespaceAndSaysNothingOfABlankOne()
    {
        return Prop.ForAll(
            PaddedLine().ToArbitrary(),
            testCase =>
            {
                // Act
                JbProgressStep? padded = JbProgressLines.Classify(testCase.Padded);

                // Assert — the padding is what a redirected console, an indented line or a stray carriage
                // return adds, and none of it is a claim about the run.
                padded.ShouldBe(
                    JbProgressLines.Classify(testCase.Line),
                    $"\"{HostileStrings.Excerpt(testCase.Padded)}\" is \"{HostileStrings.Excerpt(testCase.Line)}\" "
                    + "with whitespace on either side, so it says the same thing about the run.");

                if (testCase.Line.All(char.IsWhiteSpace))
                    padded.ShouldBeNull(
                        $"\"{HostileStrings.Excerpt(testCase.Padded)}\" is whitespace throughout, so it leaves the run in "
                        + "whatever phase it was already in.");
            });
    }

    [Property]
    public Property Classify_EveryLineJbsVocabularyCanProduce_IsReadAsThePhaseItBelongsTo()
    {
        return Prop.ForAll(
            FileName().ToArbitrary(),
            ProfileName().ToArbitrary(),
            (fileName, profileName) =>
            {
                // Act
                JbProgressStep? analyzed = JbProgressLines.Classify(JbProgressLines.AnalyzingFilePrefix + fileName);
                JbProgressStep? inspected = JbProgressLines.Classify(JbProgressLines.InspectingFilePrefix + fileName);
                JbProgressStep? cleaning = JbProgressLines.Classify(JbProgressLines.CleaningPhasePrefix + profileName);

                // Assert — a per-file line has to count for any file name jb can print, because the count is
                // what a heartbeat reports and a name it silently skips reads as a stalled run.
                analyzed.ShouldBe(
                    new JbProgressStep(JbRunPhase.Analyzing, true),
                    $"\"{JbProgressLines.AnalyzingFilePrefix}{HostileStrings.Excerpt(fileName)}\" is inspectcode analysing one file.");
                inspected.ShouldBe(
                    new JbProgressStep(JbRunPhase.Inspecting, true),
                    $"\"{JbProgressLines.InspectingFilePrefix}{HostileStrings.Excerpt(fileName)}\" is inspectcode inspecting one file.");
                cleaning.ShouldBe(
                    new JbProgressStep(JbRunPhase.Cleaning, false),
                    $"\"{JbProgressLines.CleaningPhasePrefix}{HostileStrings.Excerpt(profileName)}\" is the one line cleanupcode "
                    + "writes about where it has got to, whichever profile it names.");
            });
    }

    /// <summary>Any line at all: arbitrary strings unioned with the ones that sit on the recogniser's edges.</summary>
    private static Gen<string> AnyLine()
    {
        return HostileStrings.AnyOr(KnownLines);
    }

    private static Gen<PaddedLineCase> PaddedLine()
    {
        Gen<string> pad = Gen.Elements(WhitespaceRuns);

        return AnyLine()
            .SelectMany(_ => pad, (line, leading) => (Line: line, Leading: leading))
            .SelectMany(_ => pad, (drawn, trailing) => new PaddedLineCase(drawn.Line, drawn.Leading, trailing));
    }

    /// <summary>
    ///     A name a per-file line could carry. Two shapes are excluded, and both are accepted ambiguity
    ///     rather than a gap: a name that is nothing but whitespace leaves the prefix with nothing after it,
    ///     and a file literally called <c>files</c> spells the analysis announcement — pinned as such by the
    ///     example suite, because the announcement is the reading that matters.
    /// </summary>
    private static Gen<string> FileName()
    {
        return HostileStrings.AnyOr(KnownFileNames)
            .Where(name => name.Trim().Length > 0)
            .Where(name => (JbProgressLines.AnalyzingFilePrefix + name).Trim() != JbProgressLines.AnalyzingPhaseLine);
    }

    private static Gen<string> ProfileName()
    {
        return HostileStrings.AnyOr(KnownProfileNames);
    }

    /// <summary>A line and the whitespace put either side of it.</summary>
    private sealed record PaddedLineCase(string Line, string Leading, string Trailing)
    {
        /// <summary>The padded line, which is what gets classified.</summary>
        internal string Padded => Leading + Line + Trailing;
    }
}