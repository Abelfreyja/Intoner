using System.Runtime.InteropServices;

namespace Intoner.Services.Input;

/// <summary> prevents routed desktop input from changing the foreground application </summary>
internal static partial class DesktopInputGuard
{
    private const uint LockForegroundCode = 1;
    private const uint UnlockForegroundCode = 2;

    public static bool CanRoutePointerToCurrentProcess()
        => IsOwnedByCurrentProcess(GetForegroundWindow());

    public static bool TryLockForeground()
        => IsOwnedByCurrentProcess(GetForegroundWindow())
           && LockSetForegroundWindow(LockForegroundCode);

    public static void UnlockForeground()
        => LockSetForegroundWindow(UnlockForegroundCode);

    private static bool IsOwnedByCurrentProcess(nint window)
        => window != nint.Zero
           && NativeWindowInterop.GetProcessId(window) == Environment.ProcessId;

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LockSetForegroundWindow(uint lockCode);
}
