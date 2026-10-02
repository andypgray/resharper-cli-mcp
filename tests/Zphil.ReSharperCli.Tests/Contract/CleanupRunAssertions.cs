using Shouldly;

namespace Zphil.ReSharperCli.Tests.Contract;

/// <summary>
///     The check that a cleanup pass rewrote its file, failing with the fixture's account of the pass attached.
/// </summary>
/// <remarks>
///     <see cref="ShouldlyMethodsAttribute" /> is on the class so a failure quotes the caller's line, which is
///     the one that says which pass it was.
/// </remarks>
[ShouldlyMethods]
internal static class CleanupRunAssertions
{
    public static void ShouldHaveRewrittenTheFile(this CleanupRun run)
    {
        run.FileWasRewritten.ShouldBe(true, run.Describe());
    }
}