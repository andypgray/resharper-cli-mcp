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
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 2 of 4 file(s) changed on disk:\n"
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
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 2 of 4 file(s) changed on disk:\n"
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
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 2 of 4 file(s) changed on disk:\n"
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
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 2 of 4 file(s) changed on disk:\n"
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
            "Cleanup completed with profile \"Built-in: Full Cleanup\". 2 of 4 file(s) changed on disk. "
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
    public void Render_ManyEntriesSmallBudget_DegradesGracefullyInsteadOfMidListChop()
    {
        // Arrange — a solution-wide outcome too large to list in full. Sizing the budget to the Low rendering
        // plus headroom for the reduction note (but less than Medium's five extra "status unknown" lines)
        // proves ProgressiveRenderer walks Full -> High -> Medium (each too large) down to Low (fits) with the
        // real formatter, appending the DETAIL REDUCED note rather than hard-chopping a listing mid-line.
        List<CleanupEntry> entries = [];
        for (var i = 0; i < 20; i++) entries.Add(new CleanupEntry($"src/very/long/path/to/Changed{i:D3}.cs", CleanupFileStatus.Changed));
        for (var i = 0; i < 20; i++) entries.Add(new CleanupEntry($"src/very/long/path/to/Unchanged{i:D3}.cs", CleanupFileStatus.Unchanged));
        for (var i = 0; i < 5; i++) entries.Add(new CleanupEntry($"src/very/long/path/to/Unknown{i:D3}.cs", CleanupFileStatus.StatusUnknown));
        for (var i = 0; i < 5; i++) entries.Add(new CleanupEntry($"src/glob/**/Pattern{i:D3}.cs", CleanupFileStatus.Pattern));
        CleanupOutcome outcome = new("Built-in: Full Cleanup", entries);

        string full = CleanupSummaryFormatter.Format(outcome, DetailLevel.Full);
        int maxChars = CleanupSummaryFormatter.Format(outcome, DetailLevel.Low).Length + 200;

        // Act
        string result = ProgressiveRenderer.Render(outcome, CleanupSummaryFormatter.Format, maxChars);

        // Assert
        full.Length.ShouldBeGreaterThan(maxChars); // precondition: Full genuinely did not fit
        result.Length.ShouldBeLessThanOrEqualTo(maxChars); // note included, so the budget genuinely holds
        result.ShouldStartWith("Cleanup completed with profile \"Built-in: Full Cleanup\"."); // header intact, no mid-line chop
        result.ShouldContain("--- DETAIL REDUCED ---");
        result.ShouldContain("Reduced to Low");
        result.ShouldContain("  - src/very/long/path/to/Changed000.cs (changed)"); // changed files still listed
        result.ShouldContain("(+20 unchanged, not listed)"); // low-signal categories collapsed to counts
        result.ShouldNotContain("(unchanged)"); // no unchanged entry listed individually at Low
    }
}