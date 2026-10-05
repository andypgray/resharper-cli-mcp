using Shouldly;
using Xunit;
using Zphil.ReSharperCli.Tests.TestSupport;

namespace Zphil.ReSharperCli.Tests.Documentation;

/// <summary>Holds every project in the tree to declaring <c>RestoreLockedMode</c> itself.</summary>
/// <remarks>
///     The lock-file policy is explained once in <c>Directory.Build.props</c>, but it cannot be <em>declared</em>
///     there: OpenSSF Scorecard's Pinned-Dependencies check parses csproj files directly and never imports that
///     file, and it credits the property all-or-nothing — every tracked csproj setting it pins the whole restore
///     finding, while some setting it scores exactly as none. So a project added without the property silently
///     drops the published score rather than breaking a build, which is the failure this test exists to make
///     loud.
/// </remarks>
public sealed class RestoreLockSiteTests
{
    /// <summary>The literal Scorecard's XML unmarshalling accepts; a <c>$(Property)</c> reference forfeits the credit.</summary>
    private const string Declaration = "<RestoreLockedMode>true</RestoreLockedMode>";

    /// <summary>A floor on the committed projects, which are at least src, tests, and the contract fixture.</summary>
    private const int KnownProjectCount = 3;

    [Fact]
    public void EveryCommittedProject_DeclaresRestoreLockedMode()
    {
        // Arrange
        // Committed ones only: build output holds copies of the fixture project, and counting those would let
        // a real omission hide behind a stale copy that still had the property.
        IReadOnlyList<string> projects = RepoRoot.EnumerateCommitted("*.csproj");

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
}