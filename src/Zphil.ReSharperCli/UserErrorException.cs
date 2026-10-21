namespace Zphil.ReSharperCli;

/// <summary>
///     Represents an expected, user-facing error: bad input, a missing or ambiguous solution, the ReSharper CLI
///     not being installed, a failed <c>jb</c> run.
/// </summary>
/// <remarks>
///     <para>
///         Its message is returned to the MCP client as the tool's error and is not logged as a warning, which
///         is kept for unexpected failures.
///     </para>
///     <para>
///         Open for one purpose: a subclass that lets a caller recognise a particular expected failure and
///         restate it with knowledge the thrower did not have, such as
///         <see cref="Execution.ProcessTimeoutException" /> and <see cref="Services.JbExitCodeException" />.
///         A subclass is still handled as an expected error, because the handling matches the base type.
///     </para>
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