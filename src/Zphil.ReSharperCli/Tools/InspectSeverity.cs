namespace Zphil.ReSharperCli.Tools;

/// <summary>
///     Minimum severity for <c>resharper_inspect</c> to report. Validated at the argument-binding
///     layer by <see cref="Pipeline.EnumValidationConverterFactory" />, which lists these names back
///     to the caller on an unrecognised value. The member names are chosen so that
///     <c>.ToString().ToUpperInvariant()</c> yields the exact <c>jb --severity</c> CLI tokens
///     (<c>SUGGESTION</c>, <c>WARNING</c>, <c>ERROR</c>).
/// </summary>
internal enum InspectSeverity
{
    /// <summary>Hints and style suggestions (jb <c>SUGGESTION</c>).</summary>
    Suggestion,

    /// <summary>Potential bugs and code smells (jb <c>WARNING</c>). The default.</summary>
    Warning,

    /// <summary>Compilation errors only (jb <c>ERROR</c>).</summary>
    Error
}