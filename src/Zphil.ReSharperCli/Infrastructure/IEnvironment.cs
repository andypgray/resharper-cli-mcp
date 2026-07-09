namespace Zphil.ReSharperCli.Infrastructure;

/// <summary>
///     The single seam through which product code reads process environment variables, the current
///     working directory, and the user's home directory. Everything else is a pure function of these
///     values, which is what lets the xUnit suite run in parallel without ever mutating real process
///     state.
/// </summary>
internal interface IEnvironment
{
    /// <summary>The current working directory (used as the solution-discovery fallback root).</summary>
    string CurrentDirectory { get; }

    /// <summary>The user's home directory (anchors the dotnet-tools path, the cache home, and shared settings).</summary>
    string HomeDirectory { get; }

    /// <summary>Return the value of environment variable <paramref name="name" />, or <c>null</c> if unset.</summary>
    string? GetVariable(string name);
}