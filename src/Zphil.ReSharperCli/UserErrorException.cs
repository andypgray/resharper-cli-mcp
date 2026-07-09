namespace Zphil.ReSharperCli;

/// <summary>
///     An expected, user-facing error: bad input, a missing or ambiguous solution, the
///     ReSharper CLI not being installed, a failed <c>jb</c> run, etc. The global call-tool
///     filter catches these and returns the message to the MCP client <em>without</em> writing
///     to the file log, which is reserved for unexpected crashes.
/// </summary>
internal sealed class UserErrorException : InvalidOperationException
{
    public UserErrorException(string message) : base(message)
    {
    }

    public UserErrorException(string message, Exception innerException) : base(message, innerException)
    {
    }
}