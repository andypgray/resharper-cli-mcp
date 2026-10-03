using Zphil.ReSharperCli.Discovery;
using Zphil.ReSharperCli.Execution;
using Zphil.ReSharperCli.Sarif;
using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     What a stubbed <c>jb</c> answers, for a process-runner double that routes on the argument list it was
///     given.
/// </summary>
/// <remarks>
///     One spelling each, because these are contracts with the product rather than with any one test: the
///     version banner has to parse as <see cref="JbLocator" /> expects, and the SARIF has to parse as
///     <see cref="SarifParser" /> expects — re-spelled per test class, a change to either contract fans out
///     over every routing stub instead of costing this file alone.
/// </remarks>
internal static class JbStubs
{
    /// <summary>The build a healthy stubbed <c>jb</c> reports, and so the one a resolved config carries.</summary>
    public const string Version = "2026.1.2";

    /// <summary>The banner a healthy <c>jb</c> answers the probe with.</summary>
    public static ProcessResult VersionProbeAnswer { get; } = VersionProbeAnswerFor(Version);

    /// <summary>The banner a <c>jb</c> of build <paramref name="version" /> answers the probe with.</summary>
    public static ProcessResult VersionProbeAnswerFor(string version)
    {
        return new ProcessResult(0, $"Version: {version}", string.Empty);
    }

    /// <summary>Whether this spawn is the <c>--version</c> probe discovery makes, rather than a run.</summary>
    public static bool IsVersionProbe(IReadOnlyList<string> arguments)
    {
        return arguments.Contains("--version");
    }

    /// <summary>
    ///     The path an <c>inspectcode</c> run was told to write its SARIF report to.
    /// </summary>
    /// <remarks><see langword="null" /> when the arguments name none.</remarks>
    public static string? OutputPathOf(IReadOnlyList<string> arguments)
    {
        string? output = arguments.FirstOrDefault(argument => argument.StartsWith(InspectService.OutputArgumentPrefix, StringComparison.Ordinal));

        return output?[InspectService.OutputArgumentPrefix.Length..];
    }

    /// <summary>
    ///     Leave behind the empty SARIF report a successful <c>inspectcode</c> writes at its <c>-o=</c> path
    ///     — when the run was asked for one. The service treats a missing report file as an error, so a stub
    ///     answering exit 0 without this claims a success no real run produces.
    /// </summary>
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