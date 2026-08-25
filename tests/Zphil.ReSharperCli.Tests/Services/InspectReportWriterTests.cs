using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Services;
using Zphil.ReSharperCli.Tests.TestDoubles;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Services;

public sealed class InspectReportWriterTests : IDisposable
{
    // The injected root is what keeps this class off the shared process temp directory, so every test here
    // gets one of its own and the parallel run cannot have two of them pruning each other's files.
    private readonly FakeEnvironment _environment = new();
    private readonly string _root;

    public InspectReportWriterTests()
    {
        _root = _environment.CreateTempDirectory();
    }

    public void Dispose()
    {
        _environment.Dispose();
    }

    [Fact]
    public void WriteMarkdown_Succeeds_WritesTheContentBeneathTheReportsDirectory()
    {
        // Arrange
        InspectReportWriter writer = Writer();

        // Act
        InspectReportOutcome outcome = writer.WriteMarkdown("Found 2 issue(s) across 1 file(s):", "/sln/App.slnx");

        // Assert
        outcome.Failure.ShouldBeNull();
        File.Exists(outcome.Path).ShouldBeTrue();
        File.ReadAllText(outcome.Path).ShouldBe("Found 2 issue(s) across 1 file(s):");
        Path.GetDirectoryName(outcome.Path)
            .ShouldBe(Path.Combine(_root, InspectReportWriter.ReportsDirectoryName));
    }

    [Fact]
    public void WriteMarkdown_Succeeds_NamesTheFileAfterTheSolution()
    {
        // Arrange — the name is the only thing distinguishing one solution's reports from another's in a
        // directory shared by every solution this server is pointed at.
        InspectReportWriter writer = Writer();

        // Act
        InspectReportOutcome outcome = writer.WriteMarkdown("body", "/sln/Contoso.Billing.sln");

        // Assert
        string name = Path.GetFileName(outcome.Path);
        name.ShouldStartWith("Contoso.Billing-inspect-");
        name.ShouldEndWith(".md");
    }

    [Fact]
    public void WriteMarkdown_TwiceForOneSolution_WritesTwoDistinctFiles()
    {
        // Arrange — two inspections of one solution within a second is ordinary, so the name cannot be
        // derived from the solution and a timestamp alone.
        InspectReportWriter writer = Writer();

        // Act
        InspectReportOutcome first = writer.WriteMarkdown("first", "/sln/App.sln");
        InspectReportOutcome second = writer.WriteMarkdown("second", "/sln/App.sln");

        // Assert
        second.Path.ShouldNotBe(first.Path);
        File.ReadAllText(first.Path).ShouldBe("first");
        File.ReadAllText(second.Path).ShouldBe("second");
    }

    [Fact]
    public void WriteMarkdown_AReportPastItsRetention_PrunesItAndKeepsTheFresh()
    {
        // Arrange — one report backdated past the retention window and one left fresh, both matching the
        // naming the prune sweeps for.
        string directory = Directory.CreateDirectory(
            Path.Combine(_root, InspectReportWriter.ReportsDirectoryName)).FullName;
        string expired = WriteExisting(directory, "Old-inspect-aaaaaaaa.md");
        string fresh = WriteExisting(directory, "Old-inspect-bbbbbbbb.md");
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow - InspectReportWriter.RetentionPeriod - TimeSpan.FromDays(1));

        // Act
        InspectReportOutcome outcome = Writer().WriteMarkdown("body", "/sln/App.sln");

        // Assert
        File.Exists(expired).ShouldBeFalse();
        File.Exists(fresh).ShouldBeTrue();
        File.Exists(outcome.Path).ShouldBeTrue();
    }

    [Fact]
    public void WriteMarkdown_AnExpiredFileThatIsNotAReport_LeavesItAlone()
    {
        // Arrange — the prune runs in a directory named for this server, but it still matches only the names
        // this class writes: deleting by age alone would reach anything a user happened to put there.
        string directory = Directory.CreateDirectory(
            Path.Combine(_root, InspectReportWriter.ReportsDirectoryName)).FullName;
        string bystander = WriteExisting(directory, "notes.md");
        File.SetLastWriteTimeUtc(bystander, DateTime.UtcNow - InspectReportWriter.RetentionPeriod - TimeSpan.FromDays(1));

        // Act
        Writer().WriteMarkdown("body", "/sln/App.sln");

        // Assert
        File.Exists(bystander).ShouldBeTrue();
    }

    [Fact]
    public void WriteMarkdown_AnExpiredReportItCannotDelete_StillWritesTheNewOne()
    {
        // Arrange — the guarantee the prune's swallow exists for: housekeeping must never cost the caller
        // the report it asked for. An expired file held open is the portable way to make one delete fail on
        // Windows; where the platform allows deleting an open file the delete simply succeeds, and the
        // assertion below is the same either way.
        string directory = Directory.CreateDirectory(
            Path.Combine(_root, InspectReportWriter.ReportsDirectoryName)).FullName;
        string expired = WriteExisting(directory, "Old-inspect-cccccccc.md");
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow - InspectReportWriter.RetentionPeriod - TimeSpan.FromDays(1));
        using FileStream held = new(expired, FileMode.Open, FileAccess.Read, FileShare.None);

        // Act
        InspectReportOutcome outcome = Writer().WriteMarkdown("body", "/sln/App.sln");

        // Assert
        outcome.Failure.ShouldBeNull();
        File.ReadAllText(outcome.Path).ShouldBe("body");
    }

    [Fact]
    public void WriteMarkdown_ADirectoryItCannotCreate_ReportsTheFailureAndWarnsRatherThanThrowing()
    {
        // Arrange — a file where the reports directory belongs, which is the portable way to make the
        // creation fail. By the time this runs a jb inspection has already cost minutes, so the artifact
        // failing must not cost the caller the summary as well.
        File.WriteAllText(Path.Combine(_root, InspectReportWriter.ReportsDirectoryName), "not a directory");
        CapturingLoggerProvider logs = new();
        InspectReportWriter writer = new(_root, Logs.Capturing(logs).CreateLogger<InspectReportWriter>());

        // Act
        InspectReportOutcome outcome = writer.WriteMarkdown("body", "/sln/App.sln");

        // Assert
        outcome.Failure.ShouldNotBeNullOrWhiteSpace();
        outcome.Path.ShouldEndWith(".md"); // still names the file it meant to write
        logs.Warnings.Count.ShouldBe(1);
        logs.Warnings[0].Message.ShouldContain(outcome.Path);
    }

    private InspectReportWriter Writer()
    {
        return new InspectReportWriter(_root, NullLogger<InspectReportWriter>.Instance);
    }

    private static string WriteExisting(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "existing");

        return path;
    }
}