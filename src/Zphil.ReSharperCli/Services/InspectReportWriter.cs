using Microsoft.Extensions.Logging;
using Zphil.ReSharperCli.Execution;

namespace Zphil.ReSharperCli.Services;

/// <summary>
///     Where the report <c>resharper_inspect</c> writes ends up: one directory this server owns, one file per
///     call, and a prune of anything older than <see cref="RetentionPeriod" /> on the way past.
/// </summary>
/// <remarks>
///     <para>
///         The response names the file it wrote, which is the whole point — a solution-wide run's itemised
///         findings cannot fit a tool response, and the alternative is a raw <c>jb</c> outside the server,
///         which does not take the run queue's lock and forks a cold cache generation instead.
///     </para>
///     <para>
///         <paramref name="rootDirectory" /> is injected rather than read from
///         <see cref="Path.GetTempPath" /> in here, so a test can point it at a directory of its own and the
///         parallel xUnit run stays off one shared location — <see cref="Directory.CreateTempSubdirectory" />
///         takes only a prefix and always resolves under the process's temp path, so it cannot honour a root
///         at all. It is not a third seam: the composition root passes the real temp path.
///     </para>
///     <para>
///         A write that fails is reported, not thrown. By the time this runs, a <c>jb</c> inspection has
///         already cost minutes and the summary in the response is good; failing the whole call over the
///         artifact would throw that away, so the outcome carries the reason instead.
///     </para>
/// </remarks>
internal sealed class InspectReportWriter(string rootDirectory, ILogger<InspectReportWriter> logger)
{
    /// <summary>The directory, under the injected root, that every report is written into.</summary>
    internal const string ReportsDirectoryName = "resharper-cli-mcp-reports";

    /// <summary>Matches only the files this class writes, so the prune cannot reach anything else.</summary>
    private const string ReportSearchPattern = "*-inspect-*.md";

    /// <summary>
    ///     <c>rwx------</c>. A report carries the paths and messages of every finding in the user's source
    ///     tree, and on Unix the directory it lands in is under a shared <c>/tmp</c>. The SARIF holding the
    ///     same content already gets this, because <see cref="Directory.CreateTempSubdirectory" /> applies it
    ///     — <see cref="Directory.CreateDirectory(string)" /> does not, so it is applied here instead of
    ///     silently dropping to whatever the umask allows.
    /// </summary>
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    ///     How long a written report survives. There is a real growth to bound — a report is written on every
    ///     call that asks for one, including the ones that find nothing, so an agent working through a
    ///     solution leaves a file per call — but it is tens of kilobytes at a time, and pruning one a caller
    ///     still wanted costs a re-run measured in minutes. Hence generous rather than tight.
    /// </summary>
    internal static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(7);

    /// <summary>
    ///     Writes <paramref name="markdown" /> to a file named after <paramref name="solutionPath" />, and
    ///     returns where it went.
    /// </summary>
    /// <remarks>
    ///     The name carries a random suffix rather than a timestamp: two calls against one solution in the
    ///     same second are ordinary, and the response names the exact path, so nothing downstream has to sort
    ///     them.
    /// </remarks>
    public InspectReportOutcome WriteMarkdown(string markdown, string solutionPath)
    {
        string directory = Path.Combine(rootDirectory, ReportsDirectoryName);
        string file = Path.Combine(directory, FileName(solutionPath));

        try
        {
            Directory.CreateDirectory(directory);
            RestrictToOwner(directory);

            Prune(directory);

            // WriteAllText and not WriteAllLines: the formatter's output is \n-only ASCII by contract, and
            // the line-based overloads would rewrite it to the platform's endings.
            File.WriteAllText(file, markdown);

            // Debug, not Information: the level split here is by who is waiting, and this costs milliseconds
            // against a jb run that cost minutes. The response already names the file.
            logger.LogDebug(
                "Wrote a {CharacterCount}-character inspect report to {ReportPath}", markdown.Length, file);

            return new InspectReportOutcome(file, null);
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            // Warning: the caller asked for this file by name and is not getting it. Unlike the cache-home
            // side effects that swallow the same exception set, nothing here retries or degrades gracefully
            // on its own.
            logger.LogWarning(exception, "Could not write the inspect report to {ReportPath}", file);

            return new InspectReportOutcome(file, exception.Message);
        }
    }

    /// <summary>
    ///     <c>&lt;SolutionName&gt;-inspect-&lt;8 hex&gt;.md</c>, which is what
    ///     <see cref="ReportSearchPattern" /> matches.
    /// </summary>
    private static string FileName(string solutionPath)
    {
        string solutionName = Path.GetFileNameWithoutExtension(solutionPath);
        string suffix = Guid.NewGuid().ToString("N")[..8];

        return $"{solutionName}-inspect-{suffix}.md";
    }

    /// <summary>Owner-only where the platform has the concept, and a no-op where it does not.</summary>
    private static void RestrictToOwner(string directory)
    {
        if (OperatingSystem.IsWindows()) return;

        File.SetUnixFileMode(directory, OwnerOnly);
    }

    /// <summary>Drops reports past their retention.</summary>
    /// <remarks>
    ///     Housekeeping, so it is fenced off twice: each delete is best-effort on its own, because two servers
    ///     can prune one directory at once and a file vanishing between the listing and the delete is ordinary;
    ///     and the sweep as a whole is, because a prune that cannot run must never cost the caller the report
    ///     it asked for. The listing is materialised for the same reason — deleting out from under a lazy
    ///     enumerator is the one way this could take the write down with it.
    /// </remarks>
    private void Prune(string directory)
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow - RetentionPeriod;
            var pruned = 0;

            foreach (string file in Directory.EnumerateFiles(directory, ReportSearchPattern).ToArray())
                try
                {
                    if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;

                    File.Delete(file);
                    pruned++;
                }
                catch (Exception exception) when (FilesystemFailure.Covers(exception))
                {
                    logger.LogDebug(exception, "Could not prune the expired inspect report {ReportPath}", file);
                }

            if (pruned > 0)
                logger.LogDebug(
                    "Pruned {PrunedCount} inspect report(s) older than {RetentionDays} days from {ReportDirectory}",
                    pruned,
                    RetentionPeriod.TotalDays,
                    directory);
        }
        catch (Exception exception) when (FilesystemFailure.Covers(exception))
        {
            logger.LogDebug(exception, "Could not prune expired inspect reports from {ReportDirectory}", directory);
        }
    }
}

/// <summary>
///     Where a report went, and why it did not.
/// </summary>
/// <remarks>
///     <see cref="Failure" /> is the exception message when the write failed and <see langword="null" /> when
///     it succeeded; <see cref="Path" /> is the intended path either way, because a caller told the file could
///     not be written wants to know which file.
/// </remarks>
internal sealed record InspectReportOutcome(string Path, string? Failure);