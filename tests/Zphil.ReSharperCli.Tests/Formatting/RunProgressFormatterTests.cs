using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Formatting;

namespace Zphil.ReSharperCli.Tests.Formatting;

/// <summary>
///     The single line an MCP progress notification carries, pinned per phase. These strings are the spec:
///     they are what a caller reads while a run is in flight, and the run cap they name is how anyone ever
///     learns the cap exists before it bites.
/// </summary>
public sealed class RunProgressFormatterTests
{
    private static readonly TimeSpan Cap = TimeSpan.FromMinutes(10);

    private static readonly string SolutionPath = Path.Combine("C:", "repos", "loadbearing", "LoadBearing.slnx");

    [Fact]
    public void Format_TheFirstBeat_SaysStartingRatherThanBlamingAnotherRun()
    {
        // The first beat is immediate and lands while the run is nominally queued. At that instant nothing
        // has established that anyone else holds the cache, so "waiting for another run" would be a claim
        // about another session that has not been made.
        string message = Format(JbRunPhase.Queued, TimeSpan.FromMilliseconds(3));

        message.ShouldBe("inspectcode on LoadBearing.slnx: starting");
    }

    [Fact]
    public void Format_AGenuineQueueWait_NamesItAndLeavesTheCapOut()
    {
        // No cap in this line, deliberately: JbRunLock's wait is outside the run budget, so charging the
        // queue against the run cap would send a caller to raise the wrong number.
        string message = Format(JbRunPhase.Queued, TimeSpan.FromSeconds(123));

        message.ShouldBe(
            "inspectcode on LoadBearing.slnx: waiting for another run on this solution's ReSharper cache "
            + "— 2 minutes 3 seconds");
    }

    [Fact]
    public void Format_AResetQueuedBehindARun_ReadsAsACacheResetWaitingWithNoCap()
    {
        // A cache reset queues on the same lock and spawns nothing, so it reaches here labelled by the work
        // rather than by a jb subcommand — naming one would send a caller looking for a process that never
        // exists. Queued is the only phase it ever has, so the cap stays unarmed and unnamed throughout.
        string message = Format(JbRunPhase.Queued, TimeSpan.FromSeconds(123), "cache reset");

        message.ShouldBe(
            "cache reset on LoadBearing.slnx: waiting for another run on this solution's ReSharper cache "
            + "— 2 minutes 3 seconds");
    }

    [Fact]
    public void Format_ASeedingCopy_SaysWhereTheCacheIsComingFrom()
    {
        // A transplant runs inside the call and can copy for minutes, so it needs a phase of its own —
        // reported as a queue wait it would look like contention that is not there.
        string message = Format(JbRunPhase.Seeding, TimeSpan.FromSeconds(40));

        message.ShouldBe(
            "inspectcode on LoadBearing.slnx: copying a sibling checkout's warm cache — 40 seconds");
    }

    [Fact]
    public void Format_TheSilentPrelude_LeadsWithTheCacheStateAndNamesTheCap()
    {
        // jb spends its first half-minute loading the solution model without a word. What the cache looked
        // like going in is the best available predictor of how that silence ends, and it is already in hand.
        string message = Format(
            JbRunPhase.Starting, TimeSpan.FromSeconds(12), cacheSummary: "cold (none on disk)", cap: Cap);

        message.ShouldBe(
            "inspectcode on LoadBearing.slnx: cold (none on disk) — 12 seconds, cap 10 minutes");
    }

    [Fact]
    public void Format_TheAnalysisSweep_CountsFiles()
    {
        // The long phase of a cold run — 451 of 497 seconds on one measured solution — so this is the message
        // a caller actually sits watching.
        string message = Format(
            JbRunPhase.Analyzing, TimeSpan.FromSeconds(200), filesSeen: 402, cap: Cap);

        message.ShouldBe(
            "inspectcode on LoadBearing.slnx: analyzing 402 files — 3 minutes 20 seconds, cap 10 minutes");
    }

    [Fact]
    public void Format_TheInspectionSweep_CountsFilesToo()
    {
        string message = Format(
            JbRunPhase.Inspecting, TimeSpan.FromSeconds(482), filesSeen: 88, cap: Cap);

        message.ShouldBe(
            "inspectcode on LoadBearing.slnx: inspecting 88 files — 8 minutes 2 seconds, cap 10 minutes");
    }

    [Fact]
    public void Format_ACleanupRewritingFiles_ReportsThePhaseAndInventsNoCount()
    {
        // cleanupcode names each rewritten file as a bare path nothing can recognise, so there is no number
        // to report and none is made up.
        string message = Format(
            JbRunPhase.Cleaning, subcommand: "cleanupcode", elapsed: TimeSpan.FromSeconds(240), cap: Cap);

        message.ShouldBe(
            "cleanupcode on LoadBearing.slnx: rewriting files — 4 minutes, cap 10 minutes");
    }

    [Theory]
    [InlineData(0, "analyzing files")]
    [InlineData(1, "analyzing 1 file")]
    [InlineData(2, "analyzing 2 files")]
    public void Format_TheFileCount_ReadsAsEnglishAtEveryValue(int filesSeen, string expected)
    {
        // Zero is reachable: the phase announcement arrives ahead of the first file line.
        string message = Format(JbRunPhase.Analyzing, TimeSpan.FromSeconds(5), filesSeen: filesSeen, cap: Cap);

        message.ShouldContain(expected);
    }

    [Fact]
    public void Format_AnUnreadableCache_StillSaysSomethingRatherThanNothing()
    {
        // JbCacheState degrades to a sentence rather than to null, and it rides through here untouched.
        string message = Format(
            JbRunPhase.Starting, TimeSpan.FromSeconds(9), cacheSummary: "cache state unreadable", cap: Cap);

        message.ShouldBe(
            "inspectcode on LoadBearing.slnx: cache state unreadable — 9 seconds, cap 10 minutes");
    }

    [Fact]
    public void Format_ACapSomeoneSetInSeconds_ReadsBackAsTheValueTheyChose()
    {
        // The same rounding rule the timeout message uses, and for the same reason: a 90-second cap reported
        // as "2 minutes" contradicts the number in the user's own config.
        string message = Format(
            JbRunPhase.Analyzing,
            TimeSpan.FromSeconds(30),
            filesSeen: 12,
            cap: TimeSpan.FromSeconds(90));

        message.ShouldEndWith("30 seconds, cap 1 minute 30 seconds");
    }

    [Fact]
    public void Format_APhaseWithNoClause_ThrowsRatherThanRenderingSomethingEmpty()
    {
        // A phase added to the enum and not to the switch would otherwise reach a caller as a message with a
        // hole in it, every ten seconds, for the length of the run. The same bargain ResharperTools.CapFor
        // makes over its own enum.
        Should.Throw<ArgumentOutOfRangeException>(() => Format((JbRunPhase)99, TimeSpan.FromSeconds(5)));
    }

    private static string Format(
        JbRunPhase phase,
        TimeSpan elapsed,
        string subcommand = "inspectcode",
        int filesSeen = 0,
        string? cacheSummary = null,
        TimeSpan? cap = null)
    {
        JbRunProgressSnapshot snapshot = new(
            subcommand, SolutionPath, phase, filesSeen, cacheSummary, elapsed, cap);

        return RunProgressFormatter.Format(snapshot);
    }
}