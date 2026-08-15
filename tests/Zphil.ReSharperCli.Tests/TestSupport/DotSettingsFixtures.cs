namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     The <c>.DotSettings</c> shapes the profile-resolution tests plant on disk. Shared so the reader,
///     resolver, and tool-pipeline tests all exercise the same XML: the declared-profile entry is the one
///     thing three layers agree about, and three private copies of it could drift apart silently.
/// </summary>
internal static class DotSettingsFixtures
{
    private const string Header =
        """<wpf:ResourceDictionary xml:space="preserve" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:s="clr-namespace:System;assembly=mscorlib" xmlns:wpf="http://schemas.microsoft.com/winfx/2006/xaml/presentation">""";

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
    ///     uses to widen or narrow a rule, and so the shape of the two layers in a layer-precedence fixture.
    /// </summary>
    public static string SettingSeverity(string ruleId, string severity)
    {
        return $"""
                {Header}
                	<s:String x:Key="/Default/CodeInspection/Highlighting/InspectionSeverities/={ruleId}/@EntryIndexedValue">{severity}</s:String>
                </wpf:ResourceDictionary>
                """;
    }

    /// <summary>
    ///     The real-world break this feature was found by: the same declaration behind a comment containing
    ///     <c>--</c>, which is illegal XML and which <c>XDocument</c> rejects outright while ReSharper and
    ///     <c>jb</c> read the file without complaint. The comment spans two lines so a parse error reported
    ///     afterwards has a line number that can be checked against the original.
    /// </summary>
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

    /// <summary>
    ///     A settings file broken past what discarding comments can rescue — an unclosed element on line 3,
    ///     behind an illegal comment so the lenient retry is genuinely the pass that gives up.
    /// </summary>
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