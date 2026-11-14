using System.Text;

namespace Owner;

public static class Shared
{
    public static int Length(string value)
    {
#if LINKER_NEEDS_TEXT
        return new StringBuilder(value).Length;
#else
        return value.Length;
#endif
    }
}
