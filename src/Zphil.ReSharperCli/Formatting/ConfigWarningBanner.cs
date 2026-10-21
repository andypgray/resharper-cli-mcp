using Zphil.ReSharperCli.Discovery;

namespace Zphil.ReSharperCli.Formatting;

/// <summary>
///     Renders the <see cref="ConfigWarnings" /> a tool result must lead with, or <c>""</c> when there is
///     nothing to say.
/// </summary>
/// <remarks>
///     <para>
///         Each warning describes configuration that was silently dropped rather than an error that failed
///         the call, which is why it cannot stay in the log: the call succeeds and looks authoritative while
///         answering under configuration the caller did not choose.
///     </para>
///     <para>
///         Each tool gets only the warnings that apply to it. A settings file this server could not parse is
///         still read by <c>jb</c>, which mounts discovered files as layers itself and takes a custom
///         <c>JB_SETTINGS_PATH</c> on its command line, so inspection severities are unaffected and only the
///         cleanup profile lookup was lost. <see cref="ForInspect" /> therefore says nothing about it.
///     </para>
/// </remarks>
internal static class ConfigWarningBanner
{
    /// <summary>
    ///     Returns the banner for <c>resharper_inspect</c>: only what affects which issues are reported.
    /// </summary>
    public static string ForInspect(ConfigWarnings warnings)
    {
        return Build(MissingSettingsWarning(warnings));
    }

    /// <summary>
    ///     Returns the banner for <c>resharper_cleanup</c>: everything inspect reports, plus the settings file
    ///     this server could not read.
    /// </summary>
    /// <remarks>
    ///     The caller has to know the declared profile was not the one applied, because the fallback has already
    ///     rewritten the code that profile existed to protect.
    /// </remarks>
    public static string ForCleanup(ConfigWarnings warnings)
    {
        return Build(MissingSettingsWarning(warnings), UnreadableSettingsWarning(warnings));
    }

    private static string? MissingSettingsWarning(ConfigWarnings warnings)
    {
        if (warnings.MissingSettingsPath is not { } path) return null;

        return $"WARNING: JB_SETTINGS_PATH is set to \"{path}\" but no such file exists, so the ReSharper "
               + "settings it names were not applied to this run.";
    }

    private static string? UnreadableSettingsWarning(ConfigWarnings warnings)
    {
        if (warnings.SettingsRead is not { } failure) return null;

        return $"WARNING: could not read ReSharper settings \"{failure.Path}\" ({SingleLine(failure.Reason)}). "
               + "Any cleanup profile the file declares was ignored, so this run may have used a broader "
               + "profile than the solution intends.";
    }

    /// <summary>
    ///     Joins the applicable warnings one per line and separates them from the body with a blank line, so
    ///     the banner reads as a preamble rather than as the first line of the result.
    /// </summary>
    private static string Build(params string?[] warnings)
    {
        List<string?> applicable = warnings.Where(warning => warning is not null).ToList();
        if (applicable.Count == 0) return "";

        return string.Join("\n", applicable) + "\n\n";
    }

    /// <summary>
    ///     Flattens a reason onto one line: it is an exception message, and an embedded newline would make the
    ///     rest of it read as body text.
    /// </summary>
    internal static string SingleLine(string reason)
    {
        return reason.ReplaceLineEndings(" ").Trim();
    }
}