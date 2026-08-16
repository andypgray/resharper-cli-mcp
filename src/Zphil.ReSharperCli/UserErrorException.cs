namespace Zphil.ReSharperCli;

/// <summary>
///     An expected, user-facing error: bad input, a missing or ambiguous solution, the
///     ReSharper CLI not being installed, a failed <c>jb</c> run, etc. The global call-tool
///     filter catches these and returns the message to the MCP client <em>without</em> writing
///     to the file log, which is reserved for unexpected crashes.
/// </summary>
/// <remarks>
///     Open for one purpose: a subclass that lets a <em>caller</em> recognise a particular expected failure
///     and restate it with knowledge the thrower did not have (see
///     <see cref="Execution.ProcessTimeoutException" /> and <see cref="Services.JbExitCodeException" />).
///     The filter matches the base type, so a subclass that reaches it is still handled as an expected
///     error.
/// </remarks>
internal class UserErrorException : InvalidOperationException
{
    public UserErrorException(string message) : base(message)
    {
    }

    public UserErrorException(string message, Exception innerException) : base(message, innerException)
    {
    }
}