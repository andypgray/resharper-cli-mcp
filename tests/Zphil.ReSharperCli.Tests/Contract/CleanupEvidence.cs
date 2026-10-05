using System.Security.Cryptography;
using Zphil.ReSharperCli.Services;

namespace Zphil.ReSharperCli.Tests.Contract;

/// <summary>
///     The fixture's account of one cleanup pass, read on either side of it, which travels with the failure.
/// </summary>
/// <remarks>
///     A pass that leaves its file unchanged has still exited zero — a non-zero exit fails the fixture outright —
///     so this is the only record of what <c>jb</c> did. Every part is read so that it cannot throw, as
///     <see cref="JbContractFixture.DescribeFailure" /> explains, so a part that cannot be read says so instead.
/// </remarks>
/// <param name="Restored">The file as the restore left it, just before <c>jb</c> started.</param>
/// <param name="AfterPass">The file once <c>jb</c> had exited.</param>
/// <param name="JbNamedTheFile">
///     Whether <c>jb</c> printed the file's path; <see langword="null" /> when no output was captured.
///     <c>cleanupcode</c> prints it for every file it processes and writes that file back even when nothing in
///     it changed, so this and <see cref="WriteTimeMoved" /> say that <c>jb</c> reached the file, not that it
///     changed it. A pass over already-clean text reads "named, written, unchanged" — measured against
///     <c>jb</c> 2026.2.1.
/// </param>
/// <param name="Details">
///     The rest, rendered: the cache generations either side, the solution directory, <c>jb</c>'s output, and
///     what the server logged during the pass.
/// </param>
internal sealed record CleanupEvidence(FileFacts Restored, FileFacts AfterPass, bool? JbNamedTheFile, string Details)
{
    /// <summary>
    ///     Whether the pass moved the file's write time on — which any write does, even one that writes the same
    ///     bytes back — or <see langword="null" /> when either reading failed.
    /// </summary>
    public bool? WriteTimeMoved =>
        Restored.WrittenUtc is { } restored && AfterPass.WrittenUtc is { } afterPass ? afterPass > restored : null;
}

/// <summary>
///     One reading of the cleanup target: its text and when it was last written, both <see langword="null" />
///     when it could not be read, and the whole reading as a failure message shows it.
/// </summary>
internal sealed record FileFacts(string? Text, DateTime? WrittenUtc, string Description)
{
    public static FileFacts Read(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            DateTime written = File.GetLastWriteTimeUtc(path);
            string hash = Convert.ToHexString(SHA256.HashData(bytes))[..12];

            // Decoded the way File.ReadAllText decodes, so a byte order mark is not part of the text compared.
            using StreamReader reader = new(new MemoryStream(bytes));
            string text = reader.ReadToEnd();

            return new FileFacts(text, written, $"sha256 {hash}, {bytes.Length} bytes, written {written:O}");
        }
        catch (Exception exception)
        {
            return new FileFacts(null, null, $"unreadable: {JbContractFixture.DescribeFailure(exception)}");
        }
    }
}

/// <summary>
///     One real cleanup pass: what the product reported, and the fixture's account of the pass, which reads the
///     file's text on either side of it.
/// </summary>
/// <remarks>
///     The text as well as the product's report, because the product's own <c>Changed</c> classification is
///     itself a hash comparison, and a check that read only that would be proving the classifier against itself.
/// </remarks>
internal sealed record CleanupRun(CleanupOutcome Outcome, CleanupEvidence Evidence)
{
    /// <summary>What each part of <see cref="Signature" /> says, in its order, for whatever labels the row.</summary>
    internal const string SignatureColumns = "rewritten / named by jb / write time moved";

    /// <summary>
    ///     Whether the file's text changed across the pass, or <see langword="null" /> when either reading of it
    ///     failed.
    /// </summary>
    public bool? FileWasRewritten =>
        Evidence.Restored.Text is { } restored && Evidence.AfterPass.Text is { } afterPass
            ? !string.Equals(restored, afterPass, StringComparison.Ordinal)
            : null;

    /// <summary>The pass as one row of the soft report, in <see cref="SignatureColumns" /> order.</summary>
    public string Signature =>
        $"{SoftReport.YesNo(FileWasRewritten)} / {SoftReport.YesNo(Evidence.JbNamedTheFile)} / "
        + SoftReport.YesNo(Evidence.WriteTimeMoved);

    /// <summary>Everything the fixture knows about the pass, as a failure message carries it.</summary>
    public string Describe()
    {
        return $"The \"{Outcome.Profile}\" pass ({SignatureColumns}: {Signature})\n"
               + $"restored:           {Evidence.Restored.Description}\n"
               + $"after the pass:     {Evidence.AfterPass.Description}\n"
               + Evidence.Details;
    }
}

/// <summary>The words the soft report's observation rows share.</summary>
internal static class SoftReport
{
    /// <summary>An observation that was made either way, or <c>unknown</c> when it could not be made.</summary>
    public static string YesNo(bool? value)
    {
        return value switch
        {
            true => "yes",
            false => "no",
            null => "unknown"
        };
    }
}