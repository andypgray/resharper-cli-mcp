namespace Zphil.ReSharperCli.Infrastructure;

/// <summary>
///     The single seam through which product code reads process environment variables, the current
///     working directory, and the user's home directory.
/// </summary>
/// <remarks>
///     Everything else is a pure function of these values, which is what lets the xUnit suite run in
///     parallel without ever mutating real process state.
/// </remarks>
internal interface IEnvironment
{
    string CurrentDirectory { get; }

    string HomeDirectory { get; }

    string? GetVariable(string name);
}