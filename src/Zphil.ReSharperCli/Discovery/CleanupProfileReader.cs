using System.Security;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Zphil.ReSharperCli.Discovery;

/// <summary>Why a settings file could not be read, for reporting in the tool result rather than only the log.</summary>
internal sealed record SettingsReadFailure(string Path, string Reason);

/// <summary>
///     The outcome of reading a settings file's declared cleanup profile: the profile
///     <see cref="Name" /> when one is declared, and the <see cref="Failure" /> when the file could not be
///     read at all. Both null means "read fine, declares nothing" — a distinction a bare
///     <see langword="null" /> name cannot make, and the one the caller must report on.
/// </summary>
internal sealed record DeclaredCleanupProfile(string? Name, SettingsReadFailure? Failure);

/// <summary>
///     Reads the cleanup profile a solution declares for callers that do not pick one, from the
///     <c>SilentCleanupProfile</c> entry of a ReSharper <c>.DotSettings</c> file. That is ReSharper's own
///     "profile to use when nobody picks one" key, so a repo that already narrowed its cleanup for the IDE
///     gets the same treatment from this server. <c>jb</c> itself never reads it — <c>cleanupcode</c>
///     always falls back to Full Cleanup — so the value has to be passed on as <c>--profile</c>.
/// </summary>
internal static partial class CleanupProfileReader
{
    private const string SilentCleanupProfileKey =
        "/Default/CodeStyle/CodeCleanup/SilentCleanupProfile/@EntryValue";

    /// <summary>
    ///     The declared profile name, or <see langword="null" /> when there is no settings file, no such
    ///     entry, or the file cannot be read — the last of those carrying a
    ///     <see cref="SettingsReadFailure" /> so the caller can say so out loud. Never throws: an unreadable
    ///     settings file must degrade to the built-in default, not fail the tool call.
    /// </summary>
    public static DeclaredCleanupProfile Read(string? settingsPath, ILogger logger)
    {
        if (string.IsNullOrEmpty(settingsPath)) return new DeclaredCleanupProfile(null, null);

        try
        {
            // Read through a stream: XDocument.Load(string) resolves its argument as a URI, which is a layer
            // of platform-dependent escaping semantics this path does not need — it is already known to be
            // an existing file. Opening it directly keeps the failure modes to the IOException family below.
            using FileStream stream = File.OpenRead(settingsPath);
            XDocument document = XDocument.Load(stream);

            return new DeclaredCleanupProfile(FindDeclaredProfile(document), null);
        }
        catch (XmlException)
        {
            // Strictness is the defect: ReSharper's own settings reader accepts files XDocument rejects, so a
            // file jb reads happily would otherwise turn this whole feature off silently. Retry leniently
            // rather than give up.
            return ReadPastIllegalComments(settingsPath, logger);
        }
        catch (Exception exception) when (IsExpectedReadFailure(exception))
        {
            return Failed(settingsPath, exception, logger);
        }
    }

    /// <summary>
    ///     Normalizes a profile name from either source — this settings entry or <c>resharper_cleanup</c>'s
    ///     <c>profile</c> argument — to a usable name, or <see langword="null" /> for "nobody picked one".
    ///     Blank is not a profile: it would reach jb as <c>--profile=</c> and fail the run, so it has to read
    ///     as unset and fall through to the next source. Shared so the two entry points cannot drift apart.
    /// </summary>
    internal static string? Normalize(string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>
    ///     Second pass, reached only after a strict parse failed: discard comments and parse what is left.
    ///     The observed real-world break is <c>--</c> inside a comment, which is illegal XML and which .NET
    ///     has no lenient mode for (<c>CheckCharacters=false</c> does not relax it), while ReSharper reads
    ///     such a file without complaint. Comments never carry settings data, so dropping them is a
    ///     normalization rather than a guess — and doing it only on the failure path means a well-formed file
    ///     can never be mangled by it.
    /// </summary>
    private static DeclaredCleanupProfile ReadPastIllegalComments(string settingsPath, ILogger logger)
    {
        try
        {
            string text = File.ReadAllText(settingsPath);
            XDocument document = XDocument.Parse(StripComments(text));

            return new DeclaredCleanupProfile(FindDeclaredProfile(document), null);
        }
        catch (Exception exception) when (exception is XmlException || IsExpectedReadFailure(exception))
        {
            // Reported against the comment-stripped text, whose line numbers still match the file — the
            // remaining fault is the one worth naming, not the comment we deliberately tolerated.
            return Failed(settingsPath, exception, logger);
        }
    }

    /// <summary>
    ///     Replaces every comment with the newlines it spanned. The non-greedy match mirrors XML's own "the
    ///     first <c>--&gt;</c> ends the comment" rule, and keeping the line count intact keeps any parse
    ///     error reported afterwards pointing at the right line of the real file.
    /// </summary>
    private static string StripComments(string text)
    {
        return XmlCommentPattern().Replace(text, match => new string('\n', match.Value.Count(c => c == '\n')));
    }

    /// <summary>
    ///     The <c>SilentCleanupProfile</c> value, matched on the <c>x:Key</c> attribute's local name:
    ///     .DotSettings is XAML, and the "x" prefix is bound in the file itself, so keying off the declared
    ///     namespace would be one more thing to get wrong for no gain.
    /// </summary>
    private static string? FindDeclaredProfile(XDocument document)
    {
        foreach (XElement element in document.Descendants())
        {
            XAttribute? key = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Key");
            if (key?.Value != SilentCleanupProfileKey) continue;

            return Normalize(element.Value);
        }

        return null;
    }

    private static DeclaredCleanupProfile Failed(string settingsPath, Exception exception, ILogger logger)
    {
        logger.LogWarning(
            exception,
            "Could not read the cleanup profile from \"{SettingsPath}\". Falling back to the built-in default.",
            settingsPath);

        return new DeclaredCleanupProfile(null, new SettingsReadFailure(settingsPath, exception.Message));
    }

    /// <summary>
    ///     The failures of getting at the file's bytes at all, as opposed to parsing them. Widened past the
    ///     IOException family because <see cref="File.ReadAllText(string)" /> and
    ///     <see cref="File.OpenRead" /> can also report a denied path as one of these, and this method's
    ///     contract is that it never throws.
    /// </summary>
    private static bool IsExpectedReadFailure(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException;
    }

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlCommentPattern();
}