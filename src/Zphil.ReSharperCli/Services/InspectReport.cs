namespace Zphil.ReSharperCli.Services;

/// <summary>
///     Whether <c>resharper_inspect</c> writes the complete itemised findings to a file beside its response,
///     and in what format. Validated at the argument-binding layer by
///     <see cref="Pipeline.EnumValidationConverterFactory" />, which lists these names back to the caller on
///     an unrecognised value.
/// </summary>
/// <remarks>
///     <para>
///         An enum rather than a <c>bool</c>, for two reasons. There is no boolean coercer in
///         <c>Pipeline/</c>, so a client that sends <c>"true"</c> as a string — which they do — would fail
///         binding, while <see cref="Pipeline.EnumValidationConverterFactory" /> parses a string
///         case-insensitively and answers with the valid names. And the axis that may grow is the format:
///         <c>jb</c>'s own SARIF is the other artifact this server could hand over for nothing, since it is
///         the file <c>jb</c> already wrote.
///     </para>
///     <para>
///         What is <em>not</em> on the list is deliberate. <c>jb inspectcode</c> offers
///         <c>--format [Xml, Html, Text, Sarif]</c> but writes one report per run, and this server needs the
///         SARIF to build the summary, the severity counts and <c>Formatting.CompilationErrorNote</c>. So
///         every other format costs a second full <c>jb</c> run — minutes — whereas
///         <see cref="Markdown" /> is rendered from issues that are already parsed and costs nothing.
///     </para>
/// </remarks>
internal enum InspectReport
{
    /// <summary>No file is written; the response is the only output. The default.</summary>
    None,

    /// <summary>
    ///     Write the same markdown the response uses, at <see cref="Formatting.DetailLevel.Full" /> — every
    ///     issue on its own line with its own message.
    /// </summary>
    Markdown
}