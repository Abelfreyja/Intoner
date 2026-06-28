using System;

namespace Intoner.Objects.Rendering;

[Flags]
internal enum ScreenLineCaps
{
    None = 0,
    Start = 1,
    End = 2,
    Both = Start | End,
}

internal static class ScreenLineCapsUtility
{
    public static bool Has(ScreenLineCaps caps, ScreenLineCaps cap)
        => (caps & cap) == cap;
}

