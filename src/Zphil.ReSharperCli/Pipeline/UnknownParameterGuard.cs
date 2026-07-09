using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Zphil.ReSharperCli.Pipeline;

/// <summary>
///     Rejects tool calls that carry a JSON argument key matching no declared parameter,
///     turning a silent drop into an actionable, self-correcting error.
/// </summary>
/// <remarks>
///     <para>
///         The MCP SDK marshals JSON-RPC arguments with <c>UnmappedMemberHandling = Skip</c>,
///         so a hallucinated key (the model guessing <c>file</c>/<c>path</c>/<c>paths</c> instead
///         of the real <c>files</c> parameter) is dropped before the tool runs. The model never
///         learns it sent a typo — it sees only a downstream "missing required argument" error and
///         re-guesses. This guard inspects the raw argument keys ahead of binding and names both
///         the bad keys and the real parameter list so the next call self-corrects, mirroring the
///         forgiving-input policy of <see cref="EnumValidationConverterFactory" /> and
///         <see cref="StringArrayCoercerFactory" />.
///     </para>
///     <para>
///         The SDK <em>has</em> a dormant strict check, but it is gated on
///         <c>JsonUnmappedMemberHandling.Disallow</c> AND <c>!HasCustomParameterBinding</c>;
///         neither holds here (we register custom converters), so it never fires. Do NOT
///         "fix" this by flipping <c>UnmappedMemberHandling</c> — that path is unreachable for
///         our tools; this guard is the working equivalent.
///     </para>
///     <para>
///         Parameter names are read verbatim from <see cref="ParameterInfo.Name" />, the exact
///         source the SDK feeds into <c>AIJsonUtilities.CreateFunctionJsonSchema</c> (parameter
///         names are not snake-cased — only the tool method name is), so reflection here is
///         identical to the advertised schema, not an approximation. Context/service-bound
///         parameters (everything the SDK binds rather than reading from JSON) are excluded;
///         see <see cref="IsJsonBoundParameter" />.
///     </para>
/// </remarks>
internal static class UnknownParameterGuard
{
    private static readonly FrozenDictionary<string, ToolParamInfo> ToolParameters = BuildMap();

    /// <summary>
    ///     Returns an error message if <paramref name="arguments" /> contains a key that
    ///     matches no declared parameter of <paramref name="toolName" /> (case-insensitive),
    ///     otherwise <see langword="null" />. Only key identity is inspected — values are
    ///     never read, so a present-but-null valid argument is fine.
    /// </summary>
    internal static string? Validate(string toolName, IDictionary<string, JsonElement>? arguments)
    {
        // Unknown tool name is the SDK's dispatch concern, not ours — never block it here.
        if (!ToolParameters.TryGetValue(toolName, out ToolParamInfo? info)) return null;

        if (arguments is null || arguments.Count == 0) return null;

        List<string>? unknown = null;
        foreach (string key in arguments.Keys)
            if (!info.Lookup.Contains(key))
                (unknown ??= []).Add(key);

        if (unknown is null) return null;

        string badKeys = string.Join(", ", unknown.Select(k => $"\"{k}\""));
        string validNames = string.Join(", ", info.OrderedNames);
        return $"Unknown parameter {badKeys} on \"{toolName}\". Valid: {validNames}.";
    }

    private static FrozenDictionary<string, ToolParamInfo> BuildMap()
    {
        Dictionary<string, ToolParamInfo> map = new(StringComparer.OrdinalIgnoreCase);

        foreach (MethodInfo method in ToolAttributeDiscovery.GetToolMethods())
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>()?.Name is not { } toolName) continue;

            string[] orderedNames = method.GetParameters()
                .Where(IsJsonBoundParameter)
                .Select(p => p.Name!)
                .ToArray();

            map[toolName] = new ToolParamInfo(
                orderedNames,
                orderedNames.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
        }

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     True when a parameter is bound from JSON arguments (and thus part of the advertised
    ///     schema), false when the SDK binds it from request context or DI. In this server the only
    ///     excluded type in practice is <see cref="CancellationToken" /> (every tool) — services
    ///     arrive via primary constructors, not method parameters. The remaining types are excluded
    ///     defensively, mirroring the SDK's own augmentation set, so a future context-bound parameter
    ///     cannot become a false positive.
    /// </summary>
    private static bool IsJsonBoundParameter(ParameterInfo p)
    {
        Type t = p.ParameterType;

        if (t == typeof(CancellationToken) || t == typeof(AIFunctionArguments)) return false;

        if (typeof(IServiceProvider).IsAssignableFrom(t) || typeof(McpServer).IsAssignableFrom(t)) return false;

        if (t.IsGenericType)
        {
            Type definition = t.GetGenericTypeDefinition();
            if (definition == typeof(RequestContext<>) || definition == typeof(IProgress<>)) return false;
        }

        if (p.GetCustomAttribute<FromKeyedServicesAttribute>() is not null) return false;

        return p.Name is not null;
    }

    private sealed record ToolParamInfo(string[] OrderedNames, FrozenSet<string> Lookup);
}