using Intoner.Scene;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Services.Input;

/// <summary> owns pointer input routed to one native window </summary>
internal sealed partial class WindowPointerSession : IDisposable
{
    public static WindowPointerSession? TryCreate(nint window)
        => window == nint.Zero ? null : new WindowPointerSession(window);

    private const uint WindowMessageMouseMove = 0x0200;
    private const uint WindowMessageLeftButtonDown = 0x0201;
    private const uint WindowMessageLeftButtonUp = 0x0202;
    private const uint WindowMessageRightButtonDown = 0x0204;
    private const uint WindowMessageRightButtonUp = 0x0205;
    private const uint WindowMessageMiddleButtonDown = 0x0207;
    private const uint WindowMessageMiddleButtonUp = 0x0208;
    private const uint WindowMessageVerticalScroll = 0x0115;
    private const uint WindowMessageMouseLeave = 0x02A3;
    private const uint ChildWindowSkipUnavailable = 0x0001 | 0x0002;
    private const uint ScrollLineUp = 0;
    private const uint ScrollLineDown = 1;
    private const uint ScrollDispatchFlags = 0x0001 | 0x0002; // block reentrancy and abort hung targets
    private const uint ScrollDispatchTimeoutMilliseconds = 8;
    private const int MaxChildDepth = 16;

    private readonly nint _rootWindow;
    private readonly Lock _stateLock = new();

    private nint _capturedWindow;
    private nint _hoveredWindow;
    private Vector2 _lastPosition;
    private NativePoint _lastTargetPoint;
    private ScenePointerButtons _buttons;
    private float _scrollRemainder;
    private bool _hasPosition;
    private bool _hasTargetPoint;
    private bool _foregroundLocked;
    private bool _disposed;

    private WindowPointerSession(nint rootWindow)
    {
        _rootWindow = rootWindow;
    }

    public bool TrySend(in ScenePointerEvent pointerEvent)
    {
        lock (_stateLock)
        {
            if (_disposed
                || !NativeWindowInterop.IsWindow(_rootWindow))
            {
                return false;
            }

            if (pointerEvent.Kind == ScenePointerEventKind.Leave)
            {
                EndHoverUnsafe();
                ReleaseForegroundLockUnsafe();
                return true;
            }

            if (RequiresForegroundLock(pointerEvent.Kind)
                && !TryLockForegroundUnsafe())
            {
                return false;
            }

            if (!TryResolveTargetPoint(
                    pointerEvent.Position,
                    retainHoveredWindow: true,
                    out nint targetWindow,
                    out NativePoint targetPoint))
            {
                return false;
            }

            _lastPosition = pointerEvent.Position;
            _hasPosition = true;

            if (pointerEvent.Kind == ScenePointerEventKind.Move && _hoveredWindow != targetWindow)
            {
                EndHoverUnsafe();
            }

            bool redundantMove = pointerEvent.Kind == ScenePointerEventKind.Move
                                 && _hasTargetPoint
                                 && _hoveredWindow == targetWindow
                                 && _lastTargetPoint.X == targetPoint.X
                                 && _lastTargetPoint.Y == targetPoint.Y
                                 && _buttons == pointerEvent.Buttons;
            bool sent = redundantMove || SendPointerEvent(targetWindow, targetPoint, pointerEvent);

            if (!sent)
            {
                return false;
            }

            if (RequestsRedraw(pointerEvent.Kind))
            {
                _ = NativeWindowInterop.RequestRedraw(_rootWindow);
            }

            _buttons = pointerEvent.Buttons;
            if (pointerEvent.Kind == ScenePointerEventKind.Move)
            {
                _hoveredWindow = targetWindow;
                _lastTargetPoint = targetPoint;
                _hasTargetPoint = true;
            }

            if (pointerEvent.Kind == ScenePointerEventKind.ButtonDown && _capturedWindow == nint.Zero)
            {
                _capturedWindow = targetWindow;
            }
            else if (pointerEvent.Kind == ScenePointerEventKind.ButtonUp && _buttons == ScenePointerButtons.None)
            {
                _capturedWindow = nint.Zero;
            }

            return true;
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            CancelUnsafe();
            _disposed = true;
        }
    }

    public void Cancel()
    {
        lock (_stateLock)
        {
            if (!_disposed)
            {
                CancelUnsafe();
            }
        }
    }

    private void CancelUnsafe()
    {
        if (_hasPosition && _buttons != ScenePointerButtons.None)
        {
            ReleaseButtonsUnsafe();
        }

        EndHoverUnsafe();
        _capturedWindow = nint.Zero;
        _buttons = ScenePointerButtons.None;
        _scrollRemainder = 0f;
        ReleaseForegroundLockUnsafe();
    }

    private void ReleaseButtonsUnsafe()
    {
        ScenePointerButtons remaining = _buttons;
        foreach (ScenePointerButton button in ScenePointerButtonExtensions.OrderedButtons)
        {
            ScenePointerButtons flag = button.ToButtons();
            if ((remaining & flag) == ScenePointerButtons.None)
            {
                continue;
            }

            remaining &= ~flag;
            if (!TryResolveTargetPoint(
                    _lastPosition,
                    retainHoveredWindow: false,
                    out nint targetWindow,
                    out NativePoint targetPoint))
            {
                break;
            }

            _ = SendButton(targetWindow, targetPoint, button, remaining, down: false);
        }
    }

    private bool TryResolveTargetPoint(
        Vector2 position,
        bool retainHoveredWindow,
        out nint targetWindow,
        out NativePoint targetPoint)
    {
        targetWindow = nint.Zero;
        targetPoint = default;
        if (!TryResolveRootPoint(position, out NativePoint rootPoint))
        {
            return false;
        }

        targetWindow = _capturedWindow;
        targetPoint = rootPoint;
        if (targetWindow != nint.Zero)
        {
            _ = MapWindowPoints(_rootWindow, targetWindow, ref targetPoint, 1);
            return true;
        }

        if (retainHoveredWindow
            && TryMapToHoveredWindow(rootPoint, out targetWindow, out targetPoint))
        {
            return true;
        }

        targetWindow = ResolveChildWindow(_rootWindow, ref targetPoint);
        return targetWindow != nint.Zero;
    }

    private bool TryResolveRootPoint(Vector2 position, out NativePoint point)
    {
        point = default;
        if (!NativeWindowInterop.TryGetExtendedFrameBounds(_rootWindow, out NativeRect captureBounds)
            || !NativeWindowInterop.GetClientRect(_rootWindow, out NativeRect clientBounds))
        {
            return false;
        }

        float x = Math.Clamp(position.X, 0f, 1f);
        float y = Math.Clamp(position.Y, 0f, 1f);
        point = new NativePoint(
            captureBounds.Left + (int)MathF.Round(x * Math.Max(0, captureBounds.Width - 1)),
            captureBounds.Top + (int)MathF.Round(y * Math.Max(0, captureBounds.Height - 1)));
        return ScreenToClient(_rootWindow, ref point)
               && point.X >= clientBounds.Left
               && point.X < clientBounds.Right
               && point.Y >= clientBounds.Top
               && point.Y < clientBounds.Bottom;
    }

    private static nint ResolveChildWindow(nint rootWindow, ref NativePoint point)
    {
        nint current = rootWindow;
        for (int depth = 0; depth < MaxChildDepth; ++depth)
        {
            nint child = ChildWindowFromPointEx(current, point, ChildWindowSkipUnavailable);
            if (child == nint.Zero || child == current)
            {
                break;
            }

            _ = MapWindowPoints(current, child, ref point, 1);
            current = child;
        }

        return current;
    }

    private bool TryMapToHoveredWindow(
        NativePoint rootPoint,
        out nint hoveredWindow,
        out NativePoint hoveredPoint)
    {
        hoveredWindow = _hoveredWindow;
        hoveredPoint = rootPoint;
        if (hoveredWindow == nint.Zero
            || hoveredWindow == _rootWindow
            || !NativeWindowInterop.IsWindow(hoveredWindow))
        {
            return false;
        }

        _ = MapWindowPoints(_rootWindow, hoveredWindow, ref hoveredPoint, 1);
        return NativeWindowInterop.GetClientRect(hoveredWindow, out NativeRect bounds)
               && hoveredPoint.X >= bounds.Left
               && hoveredPoint.X < bounds.Right
               && hoveredPoint.Y >= bounds.Top
               && hoveredPoint.Y < bounds.Bottom;
    }

    private bool TryLockForegroundUnsafe()
    {
        if (_foregroundLocked)
        {
            return true;
        }

        bool locked = DesktopInputGuard.TryLockForeground();
        _foregroundLocked = locked;
        return locked;
    }

    private void ReleaseForegroundLockUnsafe()
    {
        if (!_foregroundLocked)
        {
            return;
        }

        DesktopInputGuard.UnlockForeground();
        _foregroundLocked = false;
    }

    private void EndHoverUnsafe()
    {
        nint hoveredWindow = _hoveredWindow;
        _hoveredWindow = nint.Zero;
        _hasTargetPoint = false;
        if (NativeWindowInterop.IsWindow(hoveredWindow))
        {
            _ = SendWindowMessage(hoveredWindow, WindowMessageMouseLeave, 0, nint.Zero);
        }
    }

    private static bool SendButton(
        nint window,
        NativePoint point,
        ScenePointerButton button,
        ScenePointerButtons buttons,
        bool down)
    {
        uint message = (button, down) switch
        {
            (ScenePointerButton.Left, true) => WindowMessageLeftButtonDown,
            (ScenePointerButton.Left, false) => WindowMessageLeftButtonUp,
            (ScenePointerButton.Right, true) => WindowMessageRightButtonDown,
            (ScenePointerButton.Right, false) => WindowMessageRightButtonUp,
            (ScenePointerButton.Middle, true) => WindowMessageMiddleButtonDown,
            (ScenePointerButton.Middle, false) => WindowMessageMiddleButtonUp,
            _ => 0,
        };
        return message != 0 && SendPointerMessage(window, message, buttons, point);
    }

    private bool SendPointerEvent(
        nint window,
        NativePoint point,
        in ScenePointerEvent pointerEvent)
        => pointerEvent.Kind switch
        {
            ScenePointerEventKind.Move => SendPointerMessage(
                window,
                WindowMessageMouseMove,
                pointerEvent.Buttons,
                point),
            ScenePointerEventKind.ButtonDown => SendButtonDown(window, point, pointerEvent),
            ScenePointerEventKind.ButtonUp => SendButton(
                window,
                point,
                pointerEvent.Button,
                pointerEvent.Buttons,
                down: false),
            ScenePointerEventKind.Wheel => SendScrollMessage(
                window,
                point,
                pointerEvent.WheelSteps),
            _ => false,
        };

    private bool SendButtonDown(
        nint window,
        NativePoint point,
        in ScenePointerEvent pointerEvent)
        => SendPointerMessage(window, WindowMessageMouseMove, _buttons, point)
           && SendButton(
               window,
               point,
               pointerEvent.Button,
               pointerEvent.Buttons,
               down: true);

    private static bool RequiresForegroundLock(ScenePointerEventKind kind)
        => kind is ScenePointerEventKind.ButtonDown or ScenePointerEventKind.Wheel;

    private static bool RequestsRedraw(ScenePointerEventKind kind)
        => kind is ScenePointerEventKind.ButtonUp or ScenePointerEventKind.Wheel;

    private static bool SendPointerMessage(
        nint window,
        uint message,
        ScenePointerButtons buttons,
        NativePoint point)
        => SendWindowMessage(window, message, (nuint)ToWindowsFlags(buttons), PackPoint(point));

    private static int ToWindowsFlags(ScenePointerButtons buttons)
        => ((buttons & ScenePointerButtons.Left) != ScenePointerButtons.None ? 0x0001 : 0)
           | ((buttons & ScenePointerButtons.Right) != ScenePointerButtons.None ? 0x0002 : 0)
           | ((buttons & ScenePointerButtons.Middle) != ScenePointerButtons.None ? 0x0010 : 0);

    private bool SendScrollMessage(nint window, NativePoint point, float wheelSteps)
    {
        float accumulatedSteps = _scrollRemainder + wheelSteps;
        int wholeSteps = (int)MathF.Truncate(accumulatedSteps);
        _scrollRemainder = accumulatedSteps - wholeSteps;
        if (wholeSteps == 0)
        {
            return true;
        }

        NativePoint screenPoint = point;
        return ClientToScreen(window, ref screenPoint)
               && TrySendPositionedScroll(window, screenPoint, wholeSteps);
    }

    private static bool TrySendPositionedScroll(nint window, NativePoint screenPoint, int steps)
    {
        if (!GetCursorPos(out NativePoint originalPosition))
        {
            return false;
        }

        bool movedCursor = originalPosition.X != screenPoint.X || originalPosition.Y != screenPoint.Y;
        if (movedCursor && !SetCursorPos(screenPoint.X, screenPoint.Y))
        {
            return false;
        }

        try
        {
            // single window frameworks resolve scroll targets from the shared desktop cursor
            return SendScrollCommands(window, steps);
        }
        finally
        {
            if (movedCursor)
            {
                _ = SetCursorPos(originalPosition.X, originalPosition.Y);
            }
        }
    }

    private static bool SendScrollCommands(nint window, int steps)
    {
        uint command = steps > 0 ? ScrollLineUp : ScrollLineDown;
        for (int index = 0; index < Math.Abs(steps); ++index)
        {
            if (!SendScrollCommand(window, command))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SendScrollCommand(nint window, uint command)
    {
        if (!NativeWindowInterop.IsWindow(window))
        {
            return false;
        }

        return SendMessageTimeout(
                   window,
                   WindowMessageVerticalScroll,
                   command,
                   nint.Zero,
                   ScrollDispatchFlags,
                   ScrollDispatchTimeoutMilliseconds,
                   out _) != nint.Zero;
    }

    private static bool SendWindowMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        if (!NativeWindowInterop.IsWindow(window))
        {
            return false;
        }

        return PostMessage(window, message, wParam, lParam);
    }

    private static nint PackPoint(NativePoint point)
        => unchecked((nint)((point.Y << 16) | (point.X & 0xFFFF)));

    [LibraryImport("user32.dll")]
    private static partial nint ChildWindowFromPointEx(nint parent, NativePoint point, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int MapWindowPoints(nint from, nint to, ref NativePoint point, uint pointCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(nint window, ref NativePoint point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint window, ref NativePoint point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static partial nint SendMessageTimeout(
        nint window,
        uint message,
        nuint wParam,
        nint lParam,
        uint flags,
        uint timeoutMilliseconds,
        out nuint result);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

}
