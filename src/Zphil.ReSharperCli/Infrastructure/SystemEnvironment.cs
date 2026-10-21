namespace Zphil.ReSharperCli.Infrastructure;

/// <summary>
///     Implements <see cref="IEnvironment" /> over <see cref="System.Environment" /> for production.
/// </summary>
internal sealed class SystemEnvironment : IEnvironment
{
    public string? GetVariable(string name)
    {
        return Environment.GetEnvironmentVariable(name);
    }

    public string CurrentDirectory => Environment.CurrentDirectory;

    public string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}