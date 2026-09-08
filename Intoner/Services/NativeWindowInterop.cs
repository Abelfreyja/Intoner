using System.Runtime.InteropServices;

namespace Intoner.Services;

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public NativePoint(int x, int y)
    {
        X = x;
        Y = y;
    }

    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width
        => Right - Left;

    public readonly int Height
        => Bottom - Top;
}

internal static partial class NativeWindowInterop
{
    private const uint DwmWindowAttributeExtendedFrameBounds = 9;
    private const uint DwmWindowAttributeCloaked = 14;
    private const uint RedrawInvalidate = 0x0001;
    private const uint RedrawAllChildren = 0x0080;

    public static bool TryGetExtendedFrameBounds(nint window, out NativeRect bounds)
    {
        int result = DwmGetWindowAttribute(
            window,
            DwmWindowAttributeExtendedFrameBounds,
            out bounds,
            Marshal.SizeOf<NativeRect>());
        return (result >= 0 || GetWindowRect(window, out bounds))
               && bounds.Width > 0
               && bounds.Height > 0;
    }

    public static bool IsCloaked(nint window)
    {
        int cloaked = 0;
        int result = DwmGetWindowAttribute(
            window,
            DwmWindowAttributeCloaked,
            ref cloaked,
            Marshal.SizeOf<int>());
        return result == 0 && cloaked != 0;
    }

    public static uint GetProcessId(nint window)
    {
        _ = GetWindowThreadProcessId(window, out uint processId);
        return processId;
    }

    public static bool RequestRedraw(nint window)
        => IsWindow(window)
           && RedrawWindow(
               window,
               nint.Zero,
               nint.Zero,
               RedrawInvalidate | RedrawAllChildren);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetClientRect(nint window, out NativeRect bounds);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out NativeRect bounds);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RedrawWindow(
        nint window,
        nint updateRect,
        nint updateRegion,
        uint flags);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(
        nint window,
        uint attribute,
        out NativeRect value,
        int valueSize);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(
        nint window,
        uint attribute,
        ref int value,
        int valueSize);
}
