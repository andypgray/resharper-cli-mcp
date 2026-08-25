using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     What a stubbed <c>jb</c> answers, for every process-runner double that routes on the argument list it
///     was given. One spelling each, because these are contracts with the product rather than with any one
///     test: the version banner has to parse as <see cref="Discovery.JbLocator" /> expects, and the SARIF
///     has to parse as <see cref="Sarif.SarifParser" /> expects — re-spelled per test class, a change to
///     either contract fans out over every routing stub instead of costing this file alone.
/// </summary>
internal static class JbStubs
{
    /// <summary>The banner a healthy <c>jb</c> answers the probe with.</summary>
    public static ProcessResult VersionProbeAnswer { get; } = new(0, "Version: 2026.1.2", string.Empty);

    /// <summary>Whether this spawn is the <c>--version</c> probe discovery makes, rather than a run.</summary>
    public static bool IsVersionProbe(IReadOnlyList<string> arguments)
    {
        return arguments.Contains("--version");
    }

    /// <summary>
    ///     Leave behind the empty SARIF report a successful <c>inspectcode</c> writes at its <c>-o=</c> path
    ///     — when the run was asked for one. The service treats a missing report file as an error, so a stub
    ///     answering exit 0 without this claims a success no real run produces.
    /// </summary>
    public static void WriteEmptySarifIfRequested(IReadOnlyList<string> arguments)
    {
        string? output = arguments.FirstOrDefault(argument => argument.StartsWith("-o=", StringComparison.Ordinal));
        if (output is null) return;

        File.WriteAllText(output["-o=".Length..], """{"runs":[{"results":[]}]}""");
    }
}