using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Tests.Formatting;

/// <summary>
///     The authoritative string spec for the cleanup summary. A fixed <see cref="CleanupOutcome" /> mixing
///     all four <see cref="CleanupFileStatus" /> values (in a deliberately interleaved order) is rendered at
///     each <see cref="DetailLevel" /> and pinned with an exact <c>ShouldBe</c>: Full lists every entry, the
///     middle levels progressively collapse the lower-signal categories to trailing counts, and Minimal is
///     the one-liner. Output uses <c>\n</c> line endings and is ASCII-only.
///     <para>
///         That fixture carries a wildcard, so every level pins the <em>partial</em>-measurement header. The
///         other two states have pins of their own below: a batch of named files only, which is the header
///         verbatim as it has always read, and a batch of nothing but wildcards, where there is no ratio to
///         report and the header says so.
///     </para>
/// </summary>
public sealed class CleanupSummaryFormatterTests
{
    // changed = 2 (A, D), unchanged = 1 (B), status unknown = 1 (C), pattern = 1 (lib/*.cs); concrete = 4.
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
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 2 of 4 named file(s) changed on disk:\n"
            + "  - src/A.cs (changed)\n"
            + "  - src/B.cs (unchanged)\n"
            + "  - src/C.cs (status unknown)\n"
            + "  - src/D.cs (changed)\n"
            + "  - lib/*.cs (pattern, not tracked)");
    }

    [Fact]
    public void Format_High_CollapsesUnchangedToCount()
    {
        // Act
        string summary = CleanupSummaryFormatter.Format(Mixed(), DetailLevel.High);

        // Assert
        summary.ShouldBe(
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 2 of 4 named file(s) changed on disk:\n"
            + "  - src/A.cs (changed)\n"
            + "  - src/C.cs (status unknown)\n"
            + "  - src/D.cs (changed)\n"
            + "  - lib/*.cs (pattern, not tracked)\n"
            + "  (+1 unchanged, not listed)");
    }

    [Fact]
    public void Format_Medium_CollapsesUnchangedAndPattern()
    {
        // Act
        string summary = CleanupSummaryFormatter.Format(Mixed(), DetailLevel.Medium);

        // Assert
        summary.ShouldBe(
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 2 of 4 named file(s) changed on disk:\n"
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
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 2 of 4 named file(s) changed on disk:\n"
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
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 2 of 4 named file(s) changed on disk. "
            + "(1 unchanged, 1 unknown, 1 pattern(s) not listed.)");
    }

    [Fact]
    public void Format_SingleChangedFileAtFull_IsPlainPerFileList()
    {
        // The normal small-batch case: Full output is a plain per-file list. This is exactly what an agent
        // sees after a one-file cleanup — the scenario the loadbearing probe exercises end to end.
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
            + "  - src/**/*.cs (pattern, not tracked)\n"
            + "  - tests/**/*.cs (pattern, not tracked)");
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
            + "count. (0 unchanged, 0 unknown, 2 pattern(s) not listed.)");
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
        // cleaned". Looped in a [Fact] because the internal DetailLevel cannot appear in a public test
        // method's signature (CS0051).
        foreach (DetailLevel level in Enum.GetValues<DetailLevel>())
            CleanupSummaryFormatter.DescribeReduction(level)
                .ShouldEndWith("The cleanup itself ran in full; only the report shrank.");
    }
}