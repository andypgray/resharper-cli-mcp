namespace Zphil.ReSharperCli.Services;

/// <summary>
///     The structured result of a successful <c>jb cleanupcode</c> run: the profile applied and one
///     <see cref="CleanupEntry" /> per requested <c>files</c> entry, in request order. Formatting lives in
///     <c>CleanupSummaryFormatter</c>; the service returns only data so the tool can render it at whatever
///     <c>DetailLevel</c> fits the output budget.
/// </summary>
internal sealed record CleanupOutcome(string Profile, IReadOnlyList<CleanupEntry> Entries);

/// <summary>One requested cleanup target: the path as the caller wrote it, plus what happened to it.</summary>
internal sealed record CleanupEntry(string Display, CleanupFileStatus Status);

/// <summary>What cleanup did to a single requested entry, determined by hashing the file before and after.</summary>
internal enum CleanupFileStatus
{
    /// <summary>A concrete file whose bytes differ after the run — cleanup rewrote it.</summary>
    Changed,

    /// <summary>A concrete file whose bytes are identical after the run — cleanup left it as-authored.</summary>
    Unchanged,

    /// <summary>
    ///     A concrete file whose before- or after-state could not be hashed (e.g. a transient lock); status
    ///     indeterminate.
    /// </summary>
    StatusUnknown,

    /// <summary>A wildcard pattern handed to jb unexpanded — not a single file, so not tracked.</summary>
    Pattern
}