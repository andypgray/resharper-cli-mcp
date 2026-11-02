namespace Zphil.ReSharperCli.Infrastructure;

/// <summary>
///     Defines the seam through which services read process environment variables, the current working
///     directory, and the user's home directory.
/// </summary>
/// <remarks>
///     A test hands a service its own values through a double, which is what lets the xUnit suite run in
///     parallel without ever mutating real process state.
/// </remarks>
internal interface IEnvironment
{
    string CurrentDirectory { get; }

    string HomeDirectory { get; }

    string? GetVariable(string name);
}