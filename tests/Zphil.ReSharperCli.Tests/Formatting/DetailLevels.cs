using Zphil.ReSharperCli.Formatting;

namespace Zphil.ReSharperCli.Tests.Formatting;

/// <summary>Renders something at every <see cref="DetailLevel" />, keyed by level.</summary>
/// <remarks>Keyed so that an assertion over the renderings names the level of each one it rejects.</remarks>
internal static class DetailLevels
{
    /// <summary>Returns what <paramref name="render" /> gives at each level, in ladder order.</summary>
    public static IReadOnlyDictionary<DetailLevel, string> RenderEach(Func<DetailLevel, string> render)
    {
        return Enum.GetValues<DetailLevel>().ToDictionary(level => level, render);
    }
}