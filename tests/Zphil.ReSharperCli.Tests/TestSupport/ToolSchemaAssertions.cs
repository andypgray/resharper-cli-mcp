using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>Asserts on what a client sees of this server: the advertised tools and a call's result.</summary>
[ShouldlyMethods]
internal static class ToolSchemaAssertions
{
    /// <summary>Asserts the call did not come back as an error, and returns its text.</summary>
    public static string ShouldHaveSucceeded(this CallToolResult result)
    {
        string text = TextOf(result);
        result.IsError.ShouldNotBe(true, $"the call failed with: {text}");
        return text;
    }

    /// <summary>Asserts the call came back as an error, and returns its text.</summary>
    public static string ShouldHaveFailed(this CallToolResult result)
    {
        string text = TextOf(result);
        result.IsError.ShouldBe(true, $"the call succeeded with: {text}");
        return text;
    }

    /// <summary>
    ///     Returns the one advertised tool called <paramref name="name" />, naming every advertised tool when there
    ///     is not exactly one.
    /// </summary>
    public static McpClientTool ShouldAdvertise(this IEnumerable<McpClientTool> tools, string name)
    {
        List<McpClientTool> advertised = [.. tools];
        string names = string.Join(", ", advertised.Select(tool => tool.Name));

        return advertised
            .Where(tool => tool.Name == name)
            .ShouldHaveSingleItem($"no single tool named \"{name}\" among: {names}");
    }

    /// <summary>Joins the result's text blocks, where every tool here puts its whole answer.</summary>
    private static string TextOf(CallToolResult result)
    {
        IEnumerable<string> texts = result.Content.OfType<TextContentBlock>().Select(block => block.Text);
        return string.Join("\n", texts);
    }
}