using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Sarif;
using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Provides what a stubbed <c>jb</c> answers, for a process-runner double that routes on the argument list
///     it was given.
/// </summary>
/// <remarks>
///     One spelling each, because these are contracts with the product rather than with any one test: the
///     version banner has to parse as <see cref="JbLocator" /> expects, and the SARIF has to parse as
///     <see cref="SarifParser" /> expects. Re-spelled per test class, a change to either contract would fan out
///     over every routing stub instead of costing this file alone.
/// </remarks>
internal static class JbStubs
{
    /// <summary>The build a healthy stubbed <c>jb</c> reports, and so the one a resolved config carries.</summary>
    public const string Version = "2026.1.2";

    /// <summary>Gets the banner a healthy <c>jb</c> answers the probe with.</summary>
    public static ProcessResult VersionProbeAnswer { get; } = VersionProbeAnswerFor(Version);

    /// <summary>Gets the answer of a run that succeeded and printed nothing: exit 0, empty streams.</summary>
    public static ProcessResult Success { get; } = new(0, string.Empty, string.Empty);

    /// <summary>Returns the probe banner of a <c>jb</c> at build <paramref name="version" />.</summary>
    public static ProcessResult VersionProbeAnswerFor(string version)
    {
        return new ProcessResult(0, $"Version: {version}", string.Empty);
    }

    /// <summary>Determines whether this spawn is discovery's <c>--version</c> probe rather than a run.</summary>
    public static bool IsVersionProbe(IReadOnlyList<string> arguments)
    {
        return arguments.Contains("--version");
    }

    /// <summary>
    ///     Determines whether this spawn is a <paramref name="subcommand" /> run, such as <c>inspectcode</c> or
    ///     <c>cleanupcode</c>.
    /// </summary>
    /// <remarks>
    ///     False for the version probe, which leads with <c>inspectcode</c> too
    ///     (<see cref="JbLocator.ProbeArguments" />), so a router need not have sent the probe elsewhere first.
    /// </remarks>
    public static bool IsRunOf(IReadOnlyList<string> arguments, string subcommand)
    {
        return arguments.Count > 0 && arguments[0] == subcommand && !IsVersionProbe(arguments);
    }

    /// <summary>
    ///     Answers a run the way a healthy <c>jb</c> does: writes <paramref name="sarif" /> as the report when
    ///     one was asked for, leaves the cache generation behind, and exits 0.
    /// </summary>
    /// <remarks>
    ///     Planting here makes the rule <see cref="PlantGenerationFromJbRun" /> states hold by default rather
    ///     than by memory.
    /// </remarks>
    public static ProcessResult Succeed(IReadOnlyList<string> arguments, string? sarif = null)
    {
        if (sarif is not null) WriteSarifIfRequested(arguments, sarif);

        PlantGenerationFromJbRun(arguments);
        return Success;
    }

    /// <summary>
    ///     Leaves behind what a successful <c>jb</c> run leaves: the cache generation for the solution it was
    ///     given, under the caches-home it was told to use, both read back out of the argument list the
    ///     server built.
    /// </summary>
    /// <remarks>
    ///     A stub that returns exit 0 and creates nothing claims a success no real run produces:
    ///     <see cref="JbWarmMarker.Stamp" /> then finds no directory carrying this solution's computed hash,
    ///     and the runner warns that <c>jb</c>'s naming has drifted, which is true of the double rather than of
    ///     <c>jb</c>. The warning is latched per runner, so it lands in the log of the test that ran the stub,
    ///     every time, and fails that test's own <c>Warnings.ShouldBeEmpty()</c> rather than another's.
    /// </remarks>
    private static void PlantGenerationFromJbRun(IReadOnlyList<string> arguments)
    {
        const string CachesHome = "--caches-home=";

        // jb's argument list leads with the subcommand, then the solution path.
        if (arguments.Count < 2) return;

        string? cacheHome = arguments
            .FirstOrDefault(argument => argument.StartsWith(CachesHome, StringComparison.Ordinal))?[CachesHome.Length..];

        if (string.IsNullOrEmpty(cacheHome)) return;

        CacheHomes.PlantGenerationFor(cacheHome, arguments[1]);
    }

    /// <summary>
    ///     Returns the path an <c>inspectcode</c> run was told to write its SARIF report to.
    /// </summary>
    public static string? OutputPathOf(IReadOnlyList<string> arguments)
    {
        string? output = arguments.FirstOrDefault(argument => argument.StartsWith(InspectService.OutputArgumentPrefix, StringComparison.Ordinal));

        return output?[InspectService.OutputArgumentPrefix.Length..];
    }

    /// <summary>
    ///     Leaves behind the empty SARIF report a successful <c>inspectcode</c> writes at its <c>-o=</c> path,
    ///     when the run was asked for one.
    /// </summary>
    /// <remarks>
    ///     The service treats a missing report file as an error, so a stub answering exit 0 without this claims
    ///     a success no real run produces.
    /// </remarks>
    public static void WriteEmptySarifIfRequested(IReadOnlyList<string> arguments)
    {
        WriteSarifIfRequested(arguments, """{"runs":[{"results":[]}]}""");
    }

    /// <summary>
    ///     Leaves <paramref name="sarif" /> behind as the report an <c>inspectcode</c> run writes at its <c>-o=</c>
    ///     path, when the run was asked for one.
    /// </summary>
    public static void WriteSarifIfRequested(IReadOnlyList<string> arguments, string sarif)
    {
        if (OutputPathOf(arguments) is not { } output) return;

        File.WriteAllText(output, sarif);
    }
}