using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Tests.Formatting;

/// <summary>
///     The authoritative string spec for the cleanup summary: a fixed <see cref="CleanupOutcome" /> mixing all
///     four <see cref="CleanupFileStatus" /> values, in a deliberately interleaved order, rendered at each
///     <see cref="DetailLevel" /> and pinned with an exact <c>ShouldBe</c>.
/// </summary>
/// <remarks>
///     That fixture carries a wildcard and an unreadable file, so every level pins the
///     <em>partial</em>-measurement header; the other header forms have pins of their own.
/// </remarks>
public sealed class CleanupSummaryFormatterTests
{
    // changed = 2 (A, D), unchanged = 1 (B), status unknown = 1 (C), pattern = 1 (lib/*.cs); hashed = 3 (A, B, D).
    private static CleanupOutcome Mixed()
    {
        return new CleanupOutcome(
            "Built-in: Full Cleanup",
            [
                new CleanupEntry("src/A.cs", CleanupFileStatus.Changed),
                new CleanupEntry("src/B.cs", CleanupFileStatus.Unchanged),
                new CleanupEntry("src/C.cs", CleanupFileStatus.StatusUnknown),
                new CleanupEntry("src/D.cs", CleanupFileStatus.Changed),
                new CleanupEntry("lib/*.cs", CleanupFileStatus.Pattern)
            ]);
    }

    [Fact]
    public void Format_Full_ListsEveryEntryWithStatus()
    {
        // Act
        string summary = CleanupSummaryFormatter.Format(Mixed(), DetailLevel.Full);

        // Assert
        summary.ShouldBe(
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 2 of 3 hashed file(s) changed on disk:\n"
            + "  - src/A.cs (changed)\n"
            + "  - src/B.cs (unchanged)\n"
            + "  - src/C.cs (status unknown)\n"
            + "  - src/D.cs (changed)\n"
            + "  - lib/*.cs (pattern, not hashed)");
    }

    [Fact]
    public void Format_High_CollapsesUnchangedToCount()
    {
        // Act
        string summary = CleanupSummaryFormatter.Format(Mixed(), DetailLevel.High);

        // Assert
        summary.ShouldBe(
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 2 of 3 hashed file(s) changed on disk:\n"
            + "  - src/A.cs (changed)\n"
            + "  - src/C.cs (status unknown)\n"
            + "  - src/D.cs (changed)\n"
            + "  - lib/*.cs (pattern, not hashed)\n"
            + "  (+1 unchanged, not listed)");
    }

    [Fact]
    public void Format_Medium_CollapsesUnchangedAndPattern()
    {
        // Act
        string summary = CleanupSummaryFormatter.Format(Mixed(), DetailLevel.Medium);

        // Assert
        summary.ShouldBe(
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 2 of 3 hashed file(s) changed on disk:\n"
            + "  - src/A.cs (changed)\n"
            + "  - src/C.cs (status unknown)\n"
            + "  - src/D.cs (changed)\n"
            + "  (+1 unchanged, not listed)\n"
            + "  (+1 pattern(s), not listed)");
    }

    [Fact]
    public void Format_Low_ListsChangedOnly()
    {
        // Act
        string summary = CleanupSummaryFormatter.Format(Mixed(), DetailLevel.Low);

        // Assert
        summary.ShouldBe(
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 2 of 3 hashed file(s) changed on disk:\n"
            + "  - src/A.cs (changed)\n"
            + "  - src/D.cs (changed)\n"
            + "  (+1 unchanged, not listed)\n"
            + "  (+1 status unknown, not listed)\n"
            + "  (+1 pattern(s), not listed)");
    }

    [Fact]
    public void Format_Minimal_IsSingleLineOfCounts()
    {
        // Act
        string summary = CleanupSummaryFormatter.Format(Mixed(), DetailLevel.Minimal);

        // Assert
        summary.ShouldBe(
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 2 of 3 hashed file(s) changed on disk. "
            + "(1 unchanged, 1 unknown, 1 pattern(s) not listed.)");
    }

    [Fact]
    public void Format_SingleChangedFileAtFull_IsPlainPerFileList()
    {
        // The normal small-batch case, and exactly what an agent sees after a one-file cleanup.
        CleanupOutcome outcome = new(
            "Built-in: Full Cleanup", [new CleanupEntry("src/Probe.cs", CleanupFileStatus.Changed)]);

        // Act
        string summary = CleanupSummaryFormatter.Format(outcome, DetailLevel.Full);

        // Assert
        summary.ShouldBe(
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 1 of 1 file(s) changed on disk:\n"
            + "  - src/Probe.cs (changed)");
    }

    [Fact]
    public void Format_NamedFilesOnly_CountsThemWithoutQualifyingTheDenominator()
    {
        // The ordinary batch: every entry was hashed, so the count spans everything the caller asked for and
        // the header needs no qualifier. Byte-for-byte what it has always said, which is what the factored
        // header keeps structurally true rather than duplicated across two literals.
        CleanupOutcome outcome = new(
            "Built-in: Full Cleanup",
            [
                new CleanupEntry("src/A.cs", CleanupFileStatus.Changed),
                new CleanupEntry("src/B.cs", CleanupFileStatus.Unchanged),
                new CleanupEntry("src/C.cs", CleanupFileStatus.Changed)
            ]);

        // Act
        string summary = CleanupSummaryFormatter.Format(outcome, DetailLevel.Full);

        // Assert
        summary.ShouldBe(
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 2 of 3 file(s) changed on disk:\n"
            + "  - src/A.cs (changed)\n"
            + "  - src/B.cs (unchanged)\n"
            + "  - src/C.cs (changed)");
    }

    [Fact]
    public void Format_NamedFilesAndAWildcard_QualifiesTheCountAsOverTheHashedFiles()
    {
        // Named files beside a glob. The count covers the two files hashed before and after the run, and the
        // header says so in one word; the glob is listed or counted at every level, as everywhere else.
        CleanupOutcome outcome = new(
            "Built-in: Full Cleanup",
            [
                new CleanupEntry("src/A.cs", CleanupFileStatus.Changed),
                new CleanupEntry("src/B.cs", CleanupFileStatus.Unchanged),
                new CleanupEntry("lib/*.cs", CleanupFileStatus.Pattern)
            ]);

        // Act
        string full = CleanupSummaryFormatter.Format(outcome, DetailLevel.Full);
        string minimal = CleanupSummaryFormatter.Format(outcome, DetailLevel.Minimal);

        // Assert — Minimal's tail also drops the zero category between two non-zero ones.
        full.ShouldBe(
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 1 of 2 hashed file(s) changed on disk:\n"
            + "  - src/A.cs (changed)\n"
            + "  - src/B.cs (unchanged)\n"
            + "  - lib/*.cs (pattern, not hashed)");
        minimal.ShouldBe(
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 1 of 2 hashed file(s) changed on disk. "
            + "(1 unchanged, 1 pattern(s) not listed.)");
    }

    [Fact]
    public void Format_AnUnreadableFileAmongNamedOnes_LeavesItOutOfTheCount()
    {
        // No wildcard, so a header that chose its form by the pattern count alone would print the plain form
        // over all three files and count C as compared. C was never compared: it is listed with its status,
        // and the count stops at the two files hashed both times.
        CleanupOutcome outcome = new(
            "Built-in: Full Cleanup",
            [
                new CleanupEntry("src/A.cs", CleanupFileStatus.Changed),
                new CleanupEntry("src/B.cs", CleanupFileStatus.Unchanged),
                new CleanupEntry("src/C.cs", CleanupFileStatus.StatusUnknown)
            ]);

        // Act
        string summary = CleanupSummaryFormatter.Format(outcome, DetailLevel.Full);

        // Assert
        summary.ShouldBe(
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 1 of 2 hashed file(s) changed on disk:\n"
            + "  - src/A.cs (changed)\n"
            + "  - src/B.cs (unchanged)\n"
            + "  - src/C.cs (status unknown)");
    }

    /// <summary>
    ///     The field case: an agent cleaned up with a glob, jb rewrote 26 files, and the header said
    ///     "0 of 0 file(s) changed on disk" — which the agent read as "nothing changed" and went to git to
    ///     disprove. Nothing was measured, so the header states that instead of a ratio over an empty set.
    /// </summary>
    private static CleanupOutcome AllWildcards()
    {
        return new CleanupOutcome(
            "Built-in: Full Cleanup",
            [
                new CleanupEntry("src/**/*.cs", CleanupFileStatus.Pattern),
                new CleanupEntry("tests/**/*.cs", CleanupFileStatus.Pattern)
            ]);
    }

    [Fact]
    public void Format_EveryEntryAWildcardAtFull_ReportsNoCountAndSaysWhy()
    {
        // Act
        string summary = CleanupSummaryFormatter.Format(AllWildcards(), DetailLevel.Full);

        // Assert — and it affirms the work happened, which is the job CleanupRanInFull cannot do from inside
        // a reduction note that an all-wildcard run is far too small to trigger.
        summary.ShouldBe(
            "Cleanup completed with profile \"Built-in: Full Cleanup\". Every entry was a wildcard pattern: "
            + "jb cleaned what they matched, and this server hashes named files only, so it cannot report a "
            + "count:\n"
            + "  - src/**/*.cs (pattern, not hashed)\n"
            + "  - tests/**/*.cs (pattern, not hashed)");
    }

    [Fact]
    public void Format_EveryEntryAWildcardAtMinimal_StillReportsNoCount()
    {
        // Minimal is where a squeezed budget lands, and it is the one line an agent reads whole. The header
        // must not reacquire the ratio on the way down the ladder.

        // Act
        string summary = CleanupSummaryFormatter.Format(AllWildcards(), DetailLevel.Minimal);

        // Assert
        summary.ShouldBe(
            "Cleanup completed with profile \"Built-in: Full Cleanup\". Every entry was a wildcard pattern: "
            + "jb cleaned what they matched, and this server hashes named files only, so it cannot report a "
            + "count. (2 pattern(s) not listed.)");
    }

    [Fact]
    public void Format_MinimalWhenEveryNamedFileChanged_EndsAtTheHeader()
    {
        // Minimal names only the categories it has something to count, as the listing levels' collapsed lines
        // do. With every file changed there is nothing left to count, so no parenthetical follows the header.
        CleanupOutcome outcome = new(
            "Built-in: Full Cleanup",
            [
                new CleanupEntry("src/A.cs", CleanupFileStatus.Changed),
                new CleanupEntry("src/B.cs", CleanupFileStatus.Changed)
            ]);

        // Act
        string summary = CleanupSummaryFormatter.Format(outcome, DetailLevel.Minimal);

        // Assert
        summary.ShouldBe("Cleanup completed with profile \"Built-in: Full Cleanup\". 2 of 2 file(s) changed on disk.");
    }

    [Fact]
    public void Format_EveryLevel_NeverClaimsAZeroOfZeroRatio()
    {
        // The defect in one assertion, across the whole ladder: an all-wildcard run has no denominator, so
        // no level may print one. A later header change that reintroduces the ratio fails here whichever
        // level it reintroduces it at.
        CleanupOutcome outcome = AllWildcards();

        // Act / Assert
        foreach (DetailLevel level in Enum.GetValues<DetailLevel>())
            CleanupSummaryFormatter.Format(outcome, level).ShouldNotContain("0 of 0");
    }

    /// <summary>
    ///     Three named files, none of which could be read before or after the run, for instance because another
    ///     process held them locked.
    /// </summary>
    /// <remarks>
    ///     Counting them would report "0 of 3 file(s) changed on disk" about files this server never compared,
    ///     so the header gives no count and says why.
    /// </remarks>
    private static CleanupOutcome NoneReadable()
    {
        return new CleanupOutcome(
            "Built-in: Full Cleanup",
            [
                new CleanupEntry("src/A.cs", CleanupFileStatus.StatusUnknown),
                new CleanupEntry("src/B.cs", CleanupFileStatus.StatusUnknown),
                new CleanupEntry("src/C.cs", CleanupFileStatus.StatusUnknown)
            ]);
    }

    [Fact]
    public void Format_EveryNamedFileUnreadableAtFull_ReportsNoCountAndSaysWhy()
    {
        // Act
        string summary = CleanupSummaryFormatter.Format(NoneReadable(), DetailLevel.Full);

        // Assert
        summary.ShouldBe(
            "Cleanup completed with profile \"Built-in: Full Cleanup\". Every named file was unreadable before "
            + "or after the run, so this server cannot say whether jb changed them:\n"
            + "  - src/A.cs (status unknown)\n"
            + "  - src/B.cs (status unknown)\n"
            + "  - src/C.cs (status unknown)");
    }

    [Fact]
    public void Format_EveryNamedFileUnreadableAtMinimal_StillReportsNoCount()
    {
        // Act
        string summary = CleanupSummaryFormatter.Format(NoneReadable(), DetailLevel.Minimal);

        // Assert
        summary.ShouldBe(
            "Cleanup completed with profile \"Built-in: Full Cleanup\". Every named file was unreadable before "
            + "or after the run, so this server cannot say whether jb changed them. (3 unknown not listed.)");
    }

    [Fact]
    public void Format_EveryLevel_NeverCountsAFileItCouldNotRead()
    {
        // Neither "0 of 3", a count over files never compared, nor "0 of 0", a count over nothing, at any
        // level of the ladder.
        CleanupOutcome outcome = NoneReadable();

        // Act / Assert
        foreach (DetailLevel level in Enum.GetValues<DetailLevel>())
            CleanupSummaryFormatter.Format(outcome, level).ShouldNotContain("0 of");
    }

    [Fact]
    public void Render_ManyEntriesSmallBudget_DegradesGracefullyInsteadOfMidListChop()
    {
        // Arrange — a solution-wide outcome too large to list in full, against a budget one character short
        // of the Medium rendering: Medium provably cannot fit, and Low plus its reduction note does. That
        // proves ProgressiveRenderer walks Full -> High -> Medium (each too large) down to Low (fits) with
        // the real formatter, appending the DETAIL REDUCED note rather than hard-chopping a listing mid-line.
        // The budget is sized from the renderings rather than a fixed headroom because the note counts
        // toward the fit check — a fixed number silently retunes this test to a lower level when the note's
        // wording changes.
        List<CleanupEntry> entries = [];
        for (var i = 0; i < 20; i++) entries.Add(new CleanupEntry($"src/very/long/path/to/Changed{i:D3}.cs", CleanupFileStatus.Changed));
        for (var i = 0; i < 20; i++) entries.Add(new CleanupEntry($"src/very/long/path/to/Unchanged{i:D3}.cs", CleanupFileStatus.Unchanged));
        for (var i = 0; i < 5; i++) entries.Add(new CleanupEntry($"src/very/long/path/to/Unknown{i:D3}.cs", CleanupFileStatus.StatusUnknown));
        for (var i = 0; i < 5; i++) entries.Add(new CleanupEntry($"src/glob/**/Pattern{i:D3}.cs", CleanupFileStatus.Pattern));
        CleanupOutcome outcome = new("Built-in: Full Cleanup", entries);

        string full = CleanupSummaryFormatter.Format(outcome, DetailLevel.Full);
        int maxChars = CleanupSummaryFormatter.Format(outcome, DetailLevel.Medium).Length - 1;

        // Act
        string result = ProgressiveRenderer.Render(
            outcome, CleanupSummaryFormatter.Format, maxChars, CleanupSummaryFormatter.DescribeReduction).Text;

        // Assert
        full.Length.ShouldBeGreaterThan(maxChars); // precondition: Full genuinely did not fit
        result.Length.ShouldBeLessThanOrEqualTo(maxChars); // note included, so the budget genuinely holds
        result.ShouldStartWith("Cleanup completed with profile \"Built-in: Full Cleanup\"."); // header intact, no mid-line chop
        result.ShouldContain("--- DETAIL REDUCED ---");
        result.ShouldContain("Reduced to Low");
        result.ShouldContain("only the files cleanup changed are listed"); // this domain's own description
        result.ShouldContain("  - src/very/long/path/to/Changed000.cs (changed)"); // changed files still listed
        result.ShouldContain("(+20 unchanged, not listed)"); // low-signal categories collapsed to counts
        result.ShouldNotContain("(unchanged)"); // no unchanged entry listed individually at Low
    }

    [Fact]
    public void DescribeReduction_EveryLevel_SaysTheCleanupItselfStillRanInFull()
    {
        // A shrinking report is the one place an agent could read "fewer files listed" as "fewer files
        // cleaned". A loop over the enum rather than rows, so a level added later is covered without an edit.
        foreach (DetailLevel level in Enum.GetValues<DetailLevel>())
            CleanupSummaryFormatter.DescribeReduction(level)
                .ShouldEndWith("The cleanup itself ran in full; only the report shrank.");
    }
}