using Zphil.ReSharperCli.Infrastructure;

namespace Zphil.ReSharperCli.Tests.TestDoubles;

/// <summary>
///     A hand-rolled <see cref="IEnvironment" /> for tests: dictionary-backed variables, with
///     <see cref="CurrentDirectory" /> and <see cref="HomeDirectory" /> pointed at freshly created temp
///     directories. Using this instead of mutating real process environment variables is what keeps the
///     parallel test run free of shared-state races. Dispose deletes every temp directory it created.
/// </summary>
internal sealed class FakeEnvironment : IEnvironment, IDisposable
{
    private readonly List<string> _tempDirectories = [];
    private readonly Dictionary<string, string> _variables = new(StringComparer.Ordinal);

    public FakeEnvironment()
    {
        CurrentDirectory = CreateTempDirectory();
        HomeDirectory = CreateTempDirectory();
    }

    public void Dispose()
    {
        foreach (string directory in _tempDirectories)
            try
            {
                Directory.Delete(directory, true);
            }
            catch
            {
                // Best-effort cleanup; a leaked temp dir must never fail a test.
            }
    }

    public string CurrentDirectory { get; set; }

    public string HomeDirectory { get; set; }

    public string? GetVariable(string name)
    {
        return _variables.GetValueOrDefault(name);
    }

    /// <summary>Set (or, with a <c>null</c> value, clear) an environment variable. Returns <c>this</c> for chaining.</summary>
    public FakeEnvironment SetVariable(string name, string? value)
    {
        if (value is null)
            _variables.Remove(name);
        else
            _variables[name] = value;

        return this;
    }

    /// <summary>Create a fresh temp directory tracked for deletion on <see cref="Dispose" />.</summary>
    public string CreateTempDirectory()
    {
        string directory = Directory.CreateTempSubdirectory("rscli-test-").FullName;
        _tempDirectories.Add(directory);
        return directory;
    }
}