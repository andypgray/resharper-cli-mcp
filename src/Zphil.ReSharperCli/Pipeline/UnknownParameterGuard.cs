using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Zphil.ReSharperCli.Pipeline;

/// <summary>
///     Rejects tool calls that carry a JSON argument key matching no declared parameter,
///     turning a silent drop into an actionable, self-correcting error.
/// </summary>
/// <remarks>
///     <para>
///         The MCP SDK binds each parameter by looking its name up among the call's arguments and
///         ignores every other key, so a hallucinated key (the model guessing
///         <c>file</c>/<c>path</c>/<c>paths</c> instead of the real <c>files</c> parameter) never
///         reaches the tool. The model never learns it sent a typo: a required parameter then fails
///         as missing, through the binder's <see cref="ArgumentException" />, which
///         <see cref="GlobalCallToolFilter" /> logs as unexpected, and an optional one silently takes
///         its default. This guard inspects the raw argument keys ahead of binding and names both the
///         bad keys and the real parameter list so the next call self-corrects, mirroring the
///         forgiving-input policy of <see cref="EnumValidationConverterFactory" /> and
///         <see cref="StringArrayCoercerFactory" />.
///     </para>
///     <para>
///         The SDK <em>has</em> a dormant strict check, but it needs
///         <c>JsonUnmappedMemberHandling.Disallow</c> AND no parameter bound by a
///         <c>BindParameter</c> callback. Here the Web defaults leave <c>UnmappedMemberHandling</c> at
///         <c>Skip</c>, and every tool's <c>RequestContext&lt;CallToolRequestParams&gt;</c> is bound by
///         the SDK's own <c>BindParameter</c>; custom converters are not custom parameter binding. Do
///         NOT "fix" this by flipping <c>UnmappedMemberHandling</c>: for these tools that arms nothing,
///         and where the check does fire it throws an <see cref="ArgumentException" /> that the filter
///         logs as unexpected and that names no valid parameter. This guard is the working equivalent.
///     </para>
///     <para>
///         It must accept exactly the keys the binder binds, and the SDK publishes that set itself: the
///         properties of the matched tool's <see cref="Tool.InputSchema" />, which leaves out every
///         parameter it binds from the request or from the container rather than from the arguments.
///         Reading the schema rather than reflecting over the tool method is what keeps the two from
///         drifting. Keys are compared ordinally, as the SDK's argument dictionary compares them: the Web
///         defaults' case-insensitivity applies only inside a value being deserialized, never to the keys.
///     </para>
/// </remarks>
internal static class UnknownParameterGuard
{
    /// <summary>
    ///     Returns an error message if <paramref name="arguments" /> contains a key that matches no
    ///     property of <paramref name="tool" />'s input schema.
    /// </summary>
    /// <remarks>
    ///     Keys must match exactly — ordinally, as the SDK binds them — and <see langword="null" /> means every
    ///     key did. Only key identity is inspected — values are never read, so a present-but-null valid
    ///     argument is fine.
    /// </remarks>
    internal static string? Validate(Tool tool, IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0) return null;

        List<string> validNames = ParameterNames(tool);
        List<string> unknown = arguments.Keys.Where(key => !validNames.Contains(key)).ToList();
        if (unknown.Count == 0) return null;

        string badKeys = string.Join(", ", unknown.Select(key => $"\"{key}\""));
        return $"Unknown parameter {badKeys} on \"{tool.Name}\". Valid: {string.Join(", ", validNames)}.";
    }

    /// <summary>The argument keys <paramref name="tool" /> binds, in the order its schema lists them.</summary>
    private static List<string> ParameterNames(Tool tool)
    {
        return tool.InputSchema.TryGetProperty("properties", out JsonElement properties)
            ? properties.EnumerateObject().Select(property => property.Name).ToList()
            : [];
    }
}