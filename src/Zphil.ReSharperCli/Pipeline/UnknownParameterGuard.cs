using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Zphil.ReSharperCli.Pipeline;

/// <summary>
///     Rejects tool calls whose JSON argument keys disagree with the matched tool's input schema, either way: a
///     key that matches no declared parameter, or a required parameter with no key at all. A silent drop or a
///     binder failure becomes an actionable, self-correcting error.
/// </summary>
/// <remarks>
///     <para>
///         The MCP SDK binds each parameter by looking its name up among the call's arguments and
///         ignores every other key, so a hallucinated key (the model guessing
///         <c>file</c>/<c>path</c>/<c>paths</c> instead of the real <c>files</c> parameter) never
///         reaches the tool. The model never learns it sent a typo: a required parameter then fails
///         as missing, through the binder's <see cref="ArgumentException" />, which
///         <see cref="GlobalCallToolFilter" /> logs as unexpected, and an optional one silently takes
///         its default. A call that leaves a required parameter out altogether meets the same
///         <see cref="ArgumentException" />. This guard inspects the raw argument keys ahead of binding and names
///         the bad or missing keys and the parameters the tool does take, so the next call self-corrects, in line
///         with the forgiving-input coercers in <see cref="ToolInputSerializerOptions" />.
///     </para>
///     <para>
///         The SDK has a dormant strict check, but it needs <c>JsonUnmappedMemberHandling.Disallow</c> and
///         no parameter bound by a <c>BindParameter</c> callback. Here the Web defaults leave
///         <c>UnmappedMemberHandling</c> at <c>Skip</c>, and every tool's
///         <c>RequestContext&lt;CallToolRequestParams&gt;</c> is bound by the SDK's own <c>BindParameter</c>;
///         custom converters are not custom parameter binding. Do not "fix" this by flipping
///         <c>UnmappedMemberHandling</c>: for these tools that arms nothing, and where the check does fire it
///         throws an <see cref="ArgumentException" /> that the filter logs as unexpected and that names no
///         valid parameter.
///     </para>
///     <para>
///         It must accept exactly the keys the binder binds, and the SDK publishes that set itself: the
///         properties of the matched tool's <see cref="Tool.InputSchema" />, which leaves out every
///         parameter it binds from the request or from the container rather than from the arguments. The same
///         schema's <c>required</c> array lists the parameters the binder cannot default. Reading the schema
///         rather than reflecting over the tool method is what keeps the two from drifting. Keys are compared
///         ordinally, as the SDK's argument dictionary compares them: the Web defaults' case-insensitivity
///         applies only inside a value being deserialized, never to the keys.
///     </para>
/// </remarks>
internal static class UnknownParameterGuard
{
    /// <summary>
    ///     Returns an error message if <paramref name="arguments" /> contains a key that matches no
    ///     property of <paramref name="tool" />'s input schema, or lacks a key the schema requires.
    /// </summary>
    /// <remarks>
    ///     Returns <see langword="null" /> when the keys agree with the schema. Unknown keys are reported in
    ///     preference to missing ones, since a misspelt key usually is the missing one. Values are never read, so
    ///     a present-but-null argument counts as present and is left to the tool.
    /// </remarks>
    internal static string? Validate(Tool tool, IDictionary<string, JsonElement>? arguments)
    {
        List<string> validNames = ParameterNames(tool);
        string valid = string.Join(", ", validNames);

        List<string> unknown = arguments?.Keys.Where(key => !validNames.Contains(key)).ToList() ?? [];
        if (unknown.Count > 0) return $"Unknown parameter {Quoted(unknown)} on \"{tool.Name}\". Valid: {valid}.";

        List<string> missing = RequiredNames(tool).Where(name => arguments?.ContainsKey(name) != true).ToList();
        if (missing.Count > 0) return $"Missing required parameter {Quoted(missing)} on \"{tool.Name}\". Valid: {valid}.";

        return null;
    }

    /// <summary>Returns the argument keys <paramref name="tool" /> binds, in the order its schema lists them.</summary>
    private static List<string> ParameterNames(Tool tool)
    {
        return tool.InputSchema.TryGetProperty("properties", out JsonElement properties)
            ? properties.EnumerateObject().Select(property => property.Name).ToList()
            : [];
    }

    /// <summary>Returns the argument keys <paramref name="tool" />'s schema requires, or empty when it requires none.</summary>
    private static List<string> RequiredNames(Tool tool)
    {
        return tool.InputSchema.TryGetProperty("required", out JsonElement required)
               && required.ValueKind == JsonValueKind.Array
            ? required.EnumerateArray().Select(name => name.GetString()!).ToList()
            : [];
    }

    private static string Quoted(IEnumerable<string> names)
    {
        return string.Join(", ", names.Select(name => $"\"{name}\""));
    }
}