using System.Globalization;

namespace Zphil.ReSharperCli.Tests.TestSupport;

/// <summary>
///     Runs part of a test under a chosen <see cref="CultureInfo.CurrentCulture" />, for pinning output that must
///     read the same whatever culture the machine running the server has.
/// </summary>
/// <remarks>
///     <see cref="CultureInfo.CurrentCulture" /> flows with the async context rather than belonging to the
///     process, so a scope taken in one test is invisible to the tests running beside it. Disposing the scope puts
///     back the culture it found. An empty name is the invariant culture.
/// </remarks>
internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _previous;

    public CultureScope(string name)
    {
        var culture = CultureInfo.GetCultureInfo(name);
        _previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _previous;
    }
}