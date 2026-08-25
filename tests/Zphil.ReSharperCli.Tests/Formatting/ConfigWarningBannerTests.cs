using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Formatting;
using Zphil.ReSharperCli.Pipeline;
using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Tests.Formatting;

/// <summary>
///     The string spec for the configuration-warning banner, and the budget arithmetic that keeps it alive.
///     A banner charged to the budget before the body is rendered sits outside
///     <see cref="ProgressiveRenderer" />'s reduction ladder, so it survives every step down to
///     <see cref="DetailLevel.Minimal" /> while the total still fits — which is the whole point for a
///     warning about a destructive fallback.
/// </summary>
public sealed class ConfigWarningBannerTests
{
    private static readonly SettingsReadFailure ReadFailure =
        new("C:/repo/App.sln.DotSettings", "An XML comment cannot contain '--'. Line 12, position 40.");

    [Fact]
    public void ForCleanup_NoWarnings_IsEmpty()
    {
        // Act
        string banner = ConfigWarningBanner.ForCleanup(new ConfigWarnings(null, null));

        // Assert — the ordinary case adds nothing at all, not even a blank line.
        banner.ShouldBe("");
    }

    [Fact]
    public void ForInspect_NoWarningsRecordedAtAll_IsEmpty()
    {
        // Act — ResolvedConfig can be built without warnings (the service-level tests do), so null is a
        // shape this has to tolerate rather than throw on.
        string banner = ConfigWarningBanner.ForInspect(ConfigWarnings.None);

        // Assert
        banner.ShouldBe("");
    }

    [Fact]
    public void ForCleanup_UnreadableSettings_NamesThePathTheReasonAndTheConsequence()
    {
        // Act
        string banner = ConfigWarningBanner.ForCleanup(new ConfigWarnings(null, ReadFailure));

        // Assert
        banner.ShouldBe(
            "WARNING: could not read ReSharper settings \"C:/repo/App.sln.DotSettings\" "
            + "(An XML comment cannot contain '--'. Line 12, position 40.). Any cleanup profile the file "
            + "declares was ignored, so this run may have used a broader profile than the solution "
            + "intends.\n\n");
    }

    [Fact]
    public void ForInspect_UnreadableSettings_SaysNothing()
    {
        // Act
        string banner = ConfigWarningBanner.ForInspect(new ConfigWarnings(null, ReadFailure));

        // Assert — jb reads the settings file itself, so inspection severities are intact. Only the
        // cleanup-profile lookup was lost, and inspect runs no cleanup profile.
        banner.ShouldBe("");
    }

    [Fact]
    public void ForInspect_MissingSettingsPath_NamesTheEnvironmentVariableAndThePath()
    {
        // Act
        string banner = ConfigWarningBanner.ForInspect(new ConfigWarnings("C:/repo/gone.DotSettings", null));

        // Assert — this one does reach inspect: the severities that file was to carry were never applied.
        banner.ShouldBe(
            "WARNING: JB_SETTINGS_PATH is set to \"C:/repo/gone.DotSettings\" but no such file exists, so "
            + "the ReSharper settings it names were not applied to this run.\n\n");
    }

    [Fact]
    public void ForCleanup_BothWarnings_EmitsOnePerLineEndingInABlankLine()
    {
        // Act
        string banner = ConfigWarningBanner.ForCleanup(new ConfigWarnings("C:/repo/gone.DotSettings", ReadFailure));

        // Assert
        string[] lines = banner.Split('\n');
        lines.Length.ShouldBe(4); // two warnings, then the blank line separating them from the body
        lines[0].ShouldStartWith("WARNING: JB_SETTINGS_PATH is set to");
        lines[1].ShouldStartWith("WARNING: could not read ReSharper settings");
        lines[2].ShouldBe("");
        lines[3].ShouldBe("");
    }

    [Fact]
    public void ForCleanup_ReasonSpanningLines_IsFlattenedOntoOne()
    {
        // Arrange — the reason is an exception message, and one with an embedded newline would make the
        // banner's tail read as body text.
        SettingsReadFailure failure = new("C:/repo/App.sln.DotSettings", "Access denied.\r\nRetry as admin.");

        // Act
        string banner = ConfigWarningBanner.ForCleanup(new ConfigWarnings(null, failure));

        // Assert
        banner.ShouldContain("(Access denied. Retry as admin.)");
        banner.Split('\n').Length.ShouldBe(3); // one warning line, then the blank-line separator
    }

    [Fact]
    public void Render_BodyThatWouldFitWithoutTheBanner_ReducesSoTheTotalStillFits()
    {
        // Arrange — the load-bearing arithmetic. The budget is one character short of banner + Full, so Full
        // fits the raw budget and provably does not fit the banner-charged one. Without the deduction the
        // total would overshoot and hand the truncator exactly the mid-chop the renderer exists to prevent.
        CleanupOutcome outcome = OutcomeWith(20, CleanupFileStatus.Unchanged);
        string banner = ConfigWarningBanner.ForCleanup(new ConfigWarnings(null, ReadFailure));
        string full = CleanupSummaryFormatter.Format(outcome, DetailLevel.Full);
        int maxChars = banner.Length + full.Length - 1;

        // Act
        string result = banner + Render(outcome, ResponseTruncator.BudgetForBody(maxChars, banner));

        // Assert
        full.Length.ShouldBeLessThanOrEqualTo(maxChars); // precondition: only the banner pushes it over
        result.Length.ShouldBeLessThanOrEqualTo(maxChars);
        result.ShouldStartWith("WARNING: could not read ReSharper settings");
        result.ShouldContain("--- DETAIL REDUCED ---");
    }

    [Fact]
    public void Render_BudgetThatOnlyMinimalFits_KeepsTheBannerWhole()
    {
        // Arrange — every entry changed, so Full through Low render identically and the ladder drops
        // straight through them to Minimal. The banner is the one thing that must not shrink with the body:
        // the files are already rewritten, and with the wrong profile.
        CleanupOutcome outcome = OutcomeWith(40, CleanupFileStatus.Changed);
        string banner = ConfigWarningBanner.ForCleanup(new ConfigWarnings(null, ReadFailure));
        int maxChars = banner.Length + CleanupSummaryFormatter.Format(outcome, DetailLevel.Low).Length - 1;

        // Act
        string result = banner + Render(outcome, ResponseTruncator.BudgetForBody(maxChars, banner));

        // Assert
        result.Length.ShouldBeLessThanOrEqualTo(maxChars);
        result.ShouldStartWith(banner);
        result.ShouldContain("Reduced to Minimal");
        result.ShouldContain("Cleanup completed with profile \"Built-in: Full Cleanup\". 40 of 40 file(s)");
    }

    [Fact]
    public void BudgetForBody_BannerLargerThanTheWholeBudget_StaysPositiveAndWithinTheBudget()
    {
        // Arrange — a pathological MAX_MCP_OUTPUT_TOKENS must not drive the residual negative, which would
        // print as a negative character limit in the reduction note, nor above the budget it came from.
        string banner = ConfigWarningBanner.ForCleanup(new ConfigWarnings("C:/repo/gone.DotSettings", ReadFailure));

        // Act
        int budget = ResponseTruncator.BudgetForBody(10, banner);

        // Assert
        budget.ShouldBe(10);
    }

    [Fact]
    public void BudgetForBody_NoBanner_LeavesTheBudgetExactlyAsItWas()
    {
        // A result with nothing to warn about must render byte-for-byte as it did before this banner
        // existed — including under a budget smaller than the floor, where rounding up would silently
        // un-reduce an output the client cannot afford.
        ResponseTruncator.BudgetForBody(25_000, "").ShouldBe(25_000);
        ResponseTruncator.BudgetForBody(100, "").ShouldBe(100);
    }

    private static CleanupOutcome OutcomeWith(int count, CleanupFileStatus status)
    {
        List<CleanupEntry> entries = [];
        for (var i = 0; i < count; i++)
            entries.Add(new CleanupEntry($"src/very/long/path/to/File{i:D3}.cs", status));

        return new CleanupOutcome("Built-in: Full Cleanup", entries);
    }

    private static string Render(CleanupOutcome outcome, int maxChars)
    {
        return ProgressiveRenderer.Render(
            outcome, CleanupSummaryFormatter.Format, maxChars, CleanupSummaryFormatter.DescribeReduction).Text;
    }
}