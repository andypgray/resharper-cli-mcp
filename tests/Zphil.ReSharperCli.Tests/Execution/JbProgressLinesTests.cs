using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Tests.Execution;

/// <summary>
///     What <see cref="JbProgressLines" /> makes of the lines a real <c>jb</c> prints. The sample lines here
///     are transcribed from captured runs of <c>jb</c> 2026.2.1; that they still describe the <c>jb</c> on the
///     machine is the <c>JbContract</c> suite's business, not this one's.
/// </summary>
public sealed class JbProgressLinesTests
{
    [Fact]
    public void Classify_TheAnalyzingAnnouncement_IsAPhaseRatherThanAFileNamedFiles()
    {
        // The announcement and the per-file lines share an opening word, so the wrong precedence would read
        // this as a file literally called "files" — and would then count the phase change as a file.
        JbProgressStep? step = JbProgressLines.Classify(JbProgressLines.AnalyzingPhaseLine);

        step.ShouldBe(new JbProgressStep(JbRunPhase.Analyzing, false));
    }

    [Fact]
    public void Classify_TheInspectingAnnouncement_StartsTheInspectionPhase()
    {
        JbProgressLines.Classify(JbProgressLines.InspectingPhaseLine)
            .ShouldBe(new JbProgressStep(JbRunPhase.Inspecting, false));
    }

    [Theory]
    [InlineData("Analyzing Sample.cs")]
    [InlineData("Analyzing ContractFixture.GlobalUsings.g.cs")]
    [InlineData("Analyzing src/Deeply/Nested/File.cs")]
    public void Classify_AnAnalyzedFile_CountsTowardsTheAnalysisSweep(string line)
    {
        JbProgressLines.Classify(line).ShouldBe(new JbProgressStep(JbRunPhase.Analyzing, true));
    }

    [Fact]
    public void Classify_AnInspectedFile_CountsTowardsTheInspectionSweep()
    {
        JbProgressLines.Classify("Inspecting Unused.cs")
            .ShouldBe(new JbProgressStep(JbRunPhase.Inspecting, true));
    }

    [Fact]
    public void Classify_CleanupsProfileAnnouncement_StartsTheCleaningPhase()
    {
        // cleanupcode shares none of inspectcode's vocabulary. This one line is the whole of what it says
        // about where it has got to, and it marks the moment files start being rewritten.
        JbProgressLines.Classify("Cleaning up using profile Built-in: Full Cleanup")
            .ShouldBe(new JbProgressStep(JbRunPhase.Cleaning, false));
    }

    [Fact]
    public void Classify_ACleanedFile_IsNotRecognised()
    {
        // cleanupcode names each rewritten file as a bare <project>\<path> with no prefix at all, which is
        // indistinguishable from a banner line. Reading it as a file would invent a count, and the timeout
        // message spends that count on a claim about resuming.
        JbProgressLines.Classify(@"<ContractFixture>\Misformatted.cs").ShouldBeNull();
    }

    [Theory]
    [InlineData("JetBrains Inspect Code 2026.2.1")]
    [InlineData("Configuration: Debug, Platform: Any CPU")]
    [InlineData("Enabled solution-wide analysis according to Inspect Code command line Setting.")]
    [InlineData("Inspection report was written to C:\\Temp\\results.json")]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_EverythingElse_SaysNothing(string line)
    {
        // Unrecognised leaves the run in the phase it was already in, which is why losing the vocabulary
        // costs a quieter notification rather than a wrong one.
        JbProgressLines.Classify(line).ShouldBeNull();
    }

    [Theory]
    [InlineData("Analyzing")]
    [InlineData("Inspecting")]
    public void Classify_APrefixWithNothingAfterIt_IsNotAFile(string line)
    {
        JbProgressLines.Classify(line).ShouldBeNull();
    }

    [Fact]
    public void Classify_ALineJbWroteWithCrLf_ReadsTheSameOnceTheReaderHasTrimmedIt()
    {
        // ProcessRunner strips the carriage return before this ever sees a line; the trim here is the
        // belt-and-braces for indented or padded output, and this pins that the two agree.
        JbProgressLines.Classify("  Analyzing Sample.cs  ")
            .ShouldBe(new JbProgressStep(JbRunPhase.Analyzing, true));
    }
}