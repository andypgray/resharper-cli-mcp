using System;

namespace ContractFixture;

/// <summary>
///     Warning-tier bait: unused members, an unread field, a dead local, a redundant qualifier, and a using
///     directive <c>ImplicitUsings</c> already covers. This is the tier <c>resharper_inspect</c> reports by
///     default, so a release that stopped reporting it would change what every caller of this server sees.
/// </summary>
internal class Unused
{
    private readonly int _neverRead = 42;

    private int Compute(int input, int unusedParameter)
    {
        int deadLocal = input * 2;

        return this.Double(input);
    }

    private int Double(int value)
    {
        return value * 2;
    }

    private void NeverCalled()
    {
    }
}
