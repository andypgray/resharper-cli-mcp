using System.Xml;
using System.Xml.Linq;
using Serilog;

namespace Zphil.ReSharperCli.Discovery;

/// <summary>
///     Reads the cleanup profile a solution declares for callers that do not pick one, from the
///     <c>SilentCleanupProfile</c> entry of a ReSharper <c>.DotSettings</c> file. That is ReSharper's own
///     "profile to use when nobody picks one" key, so a repo that already narrowed its cleanup for the IDE
///     gets the same treatment from this server. <c>jb</c> itself never reads it — <c>cleanupcode</c>
///     always falls back to Full Cleanup — so the value has to be passed on as <c>--profile</c>.
/// </summary>
internal static class CleanupProfileReader
{
    private const string SilentCleanupProfileKey =
        "/Default/CodeStyle/CodeCleanup/SilentCleanupProfile/@EntryValue";

    /// <summary>
    ///     The declared profile name, or <see langword="null" /> when there is no settings file, no such
    ///     entry, or the file cannot be parsed. Never throws: a malformed or unreadable settings file must
    ///     degrade to the built-in default, not fail the tool call.
    /// </summary>
    public static string? Read(string? settingsPath)
    {
        if (string.IsNullOrEmpty(settingsPath)) return null;

        try
        {
            XDocument document = XDocument.Load(settingsPath);

            // Matched on the x:Key attribute's local name: .DotSettings is XAML, and the "x" prefix is
            // bound in the file itself, so keying off the declared namespace would be one more thing to
            // get wrong for no gain.
            foreach (XElement element in document.Descendants())
            {
                XAttribute? key = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Key");
                if (key?.Value != SilentCleanupProfileKey) continue;

                string profile = element.Value.Trim();
                return profile.Length == 0 ? null : profile;
            }
        }
        catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
        {
            Log.Warning(
                exception,
                "Could not read the cleanup profile from \"{SettingsPath}\". Falling back to the built-in default.",
                settingsPath);
        }

        return null;
    }
}
