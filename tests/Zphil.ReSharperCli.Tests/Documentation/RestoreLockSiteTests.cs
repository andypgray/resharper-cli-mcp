using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Documentation;

/// <summary>
///     Holds every project in the tree to declaring <c>RestoreLockedMode</c> itself. The lock-file policy is
///     explained once in <c>Directory.Build.props</c>, but it cannot be <em>declared</em> there: OpenSSF
///     Scorecard's Pinned-Dependencies check parses csproj files directly and never imports that file, and it
///     credits the property all-or-nothing — every tracked csproj setting it pins the whole restore finding,
///     while some setting it scores exactly as none. So a fourth project added without the property silently
///     drops the published score rather than breaking a build, which is the failure this test exists to make
///     loud.
/// </summary>
public sealed class RestoreLockSiteTests
{
    /// <summary>The literal Scorecard's XML unmarshalling accepts; a <c>$(Property)</c> reference forfeits the credit.</summary>
    private const string Declaration = "<RestoreLockedMode>true</RestoreLockedMode>";

    /// <summary>The projects committed today: src, tests, and the contract fixture.</summary>
    private const int KnownProjectCount = 3;

    /// <summary>Build output carries copies of the fixture csproj that git does not track.</summary>
    private static readonly string[] ExcludedSegments = ["bin", "obj", "artifacts"];

    [Fact]
    public void EveryCommittedProject_DeclaresRestoreLockedMode()
    {
        // Arrange
        IReadOnlyList<string> projects = CommittedProjects();

        // Assert — the count first, so a broken exclusion cannot pass this test vacuously.
        projects.Count.ShouldBeGreaterThanOrEqualTo(
            KnownProjectCount,
            $"Expected at least {KnownProjectCount} csproj files under {RepoRoot.Location}; finding fewer "
            + "means the enumeration stopped meaning the set git tracks, and the check below proves nothing.");

        foreach (string project in projects)
            File.ReadAllText(project).ShouldContain(
                Declaration,
                Case.Sensitive,
                $"{Path.GetRelativePath(RepoRoot.Location, project)} must declare {Declaration} under a "
                + "'$(CI)' == 'true' PropertyGroup. Scorecard's Pinned-Dependencies check reads csproj files "
                + "only and credits this all-or-nothing, so one project missing it scores the same as none "
                + "of them having it. Directory.Build.props explains the policy; it cannot declare it.");
    }

    /// <summary>
    ///     The csproj files git tracks — build output holds copies of the fixture project, and counting those
    ///     would let a real omission hide behind a stale copy that still had the property.
    /// </summary>
    private static IReadOnlyList<string> CommittedProjects()
    {
        return Directory.EnumerateFiles(RepoRoot.Location, "*.csproj", SearchOption.AllDirectories)
            .Where(project => !IsBuildOutput(project))
            .ToList();
    }

    private static bool IsBuildOutput(string project)
    {
        string relative = Path.GetRelativePath(RepoRoot.Location, project);
        string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return segments.Any(segment => ExcludedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }
}