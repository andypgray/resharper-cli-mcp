namespace Zphil.ReSharperCli.Services;

/// <summary>
///     Defines how much detail <c>resharper_inspect</c>'s response carries, as a cap on the reduction ladder
///     rather than a level it is pinned to.
/// </summary>
/// <remarks>
///     <para>
///         <c>Formatting.ProgressiveRenderer</c> begins at the requested level and may still step below it when
///         the rendering does not fit the output budget. Pinning would hand the downstream truncator an
///         over-budget string, which is the mid-list chop the ladder exists to prevent. The
///         <c>--- DETAIL REDUCED ---</c> note says which of the two happened.
///     </para>
///     <para>
///         Response shaping only. Unlike <c>files</c> and <see cref="InspectSeverity" />, no <c>jb</c>
///         argument moves: the same analysis runs and the same issues are parsed, and this decides only how
///         many of them the response spells out. So it costs nothing and saves nothing in run time.
///     </para>
///     <para>
///         Its own enum rather than <see cref="Formatting.DetailLevel" />, so a rename inside the formatting
///         ladder cannot change the published MCP schema. <c>ResharperTools.CapFor</c> maps between them member
///         by member, so a renamed member fails to compile and an unmapped one throws rather than landing on a
///         wrong level.
///     </para>
/// </remarks>
internal enum InspectDetail
{
    /// <summary>
    ///     Every issue on its own line with its file, line, severity, rule and message. The default, and the
    ///     no-cap case: the ladder decides on its own.
    /// </summary>
    Full,

    /// <summary>
    ///     Issues repeating a rule within a file collapse to one line carrying their line numbers and one
    ///     example message.
    /// </summary>
    High,

    /// <summary>Only the most-affected files are listed; the rest are counted.</summary>
    Medium,

    /// <summary>The per-file listing is replaced by a rollup of the top rules and the top files.</summary>
    Low,

    /// <summary>One line: totals, severity counts, and the top rules.</summary>
    Minimal
}