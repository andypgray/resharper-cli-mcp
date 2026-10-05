namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>The <c>.DotSettings</c> shapes a test plants on disk.</summary>
/// <remarks>
///     Shared so every layer that reads the declared-profile entry is tested against the same XML: private
///     copies of it could drift apart silently.
/// </remarks>
internal static class DotSettingsFixtures
{
    private const string Header =
        """<wpf:ResourceDictionary xml:space="preserve" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:s="clr-namespace:System;assembly=mscorlib" xmlns:wpf="http://schemas.microsoft.com/winfx/2006/xaml/presentation">""";

    /// <summary>
    ///     Writes <paramref name="content" /> as the settings file beside the solution called
    ///     <paramref name="solutionFileName" /> in <paramref name="directory" /> — the
    ///     <c>{solution}.DotSettings</c> layer <c>jb</c> and the resolver both find by name — and returns its path.
    /// </summary>
    public static string PlantBeside(string directory, string content, string solutionFileName = "App.sln")
    {
        string path = Path.Combine(directory, solutionFileName + ".DotSettings");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>An ordinary IDE-generated settings file declaring <paramref name="profileName" />.</summary>
    public static string Declaring(string profileName)
    {
        return $"""
                {Header}
                	<s:String x:Key="/Default/CodeStyle/CodeCleanup/SilentCleanupProfile/@EntryValue">{profileName}</s:String>
                </wpf:ResourceDictionary>
                """;
    }

    /// <summary>
    ///     A settings file carrying one inspection-severity override — the entry a solution or project layer
    ///     uses to widen or narrow a rule.
    /// </summary>
    /// <remarks>The shape of each of the two layers in a layer-precedence fixture.</remarks>
    public static string SettingSeverity(string ruleId, string severity)
    {
        return $"""
                {Header}
                	<s:String x:Key="/Default/CodeInspection/Highlighting/InspectionSeverities/={ruleId}/@EntryIndexedValue">{severity}</s:String>
                </wpf:ResourceDictionary>
                """;
    }

    /// <summary>
    ///     The same declaration behind a comment containing <c>--</c>: a real-world shape, and illegal XML,
    ///     which <c>XDocument</c> rejects outright while ReSharper and <c>jb</c> read the file without complaint.
    /// </summary>
    /// <remarks>
    ///     The comment spans two lines so a parse error reported afterwards has a line number that can be
    ///     checked against the original.
    /// </remarks>
    public static string DeclaringBehindIllegalComment(string profileName)
    {
        return $"""
                {Header}
                	<!-- jb cleanupcode does not read this key, so a direct
                	     CLI run needs --profile regardless. -->
                	<s:String x:Key="/Default/CodeStyle/CodeCleanup/SilentCleanupProfile/@EntryValue">{profileName}</s:String>
                </wpf:ResourceDictionary>
                """;
    }

    /// <summary>A settings file broken past what discarding comments can rescue.</summary>
    /// <remarks>
    ///     An unclosed element on line 3, behind an illegal comment so the lenient retry is genuinely the pass
    ///     that gives up.
    /// </remarks>
    public static string Unparseable()
    {
        return $"""
                {Header}
                	<!-- broken -- beyond repair -->
                	<s:String x:Key="/Default/CodeStyle/CodeCleanup/SilentCleanupProfile/@EntryValue">Never Read
                </wpf:ResourceDictionary>
                """;
    }
}