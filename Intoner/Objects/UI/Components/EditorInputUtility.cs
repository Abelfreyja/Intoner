using Dalamud.Bindings.ImGui;
using Intoner.Scene;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI.Components;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct EditorScreenArea(Vector2 Min, Vector2 Max)
{
    public bool Contains(Vector2 point)
        => EditorInputUtility.HasArea(Min, Max)
           && point.X >= Min.X
           && point.Y >= Min.Y
           && point.X <= Max.X
           && point.Y <= Max.Y;

    public bool Intersects(Vector2 min, Vector2 max)
        => EditorInputUtility.HasArea(Min, Max)
           && EditorInputUtility.HasArea(min, max)
           && min.X < Max.X
           && min.Y < Max.Y
           && max.X > Min.X
           && max.Y > Min.Y;
}

internal static class EditorInputUtility
{
    public static ScenePointerButtons CaptureScenePointerButtons(bool includeAuxiliaryButtons = false)
    {
        const int virtualKeyLeftButton = 0x01;
        const int virtualKeyRightButton = 0x02;
        const int virtualKeyMiddleButton = 0x04;
        const int keyDownMask = 0x8000;
        ScenePointerButtons buttons = ScenePointerButtons.None;
        if ((GetKeyState(virtualKeyLeftButton) & keyDownMask) != 0)
        {
            buttons |= ScenePointerButtons.Left;
        }

        if (includeAuxiliaryButtons && (GetKeyState(virtualKeyRightButton) & keyDownMask) != 0)
        {
            buttons |= ScenePointerButtons.Right;
        }

        if (includeAuxiliaryButtons && (GetKeyState(virtualKeyMiddleButton) & keyDownMask) != 0)
        {
            buttons |= ScenePointerButtons.Middle;
        }

        return buttons;
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    public static bool HasArea(Vector2 min, Vector2 max)
        => max.X > min.X && max.Y > min.Y;

    public static bool IsMouseInside(Vector2 min, Vector2 max)
    {
        if (!HasArea(min, max))
        {
            return false;
        }

        Vector2 mouse = ImGui.GetMousePos();
        return mouse.X >= min.X
            && mouse.Y >= min.Y
            && mouse.X <= max.X
            && mouse.Y <= max.Y;
    }

    public static bool IsMouseClickedInside(Vector2 min, Vector2 max, ImGuiMouseButton button = ImGuiMouseButton.Left)
        => ImGui.IsMouseClicked(button) && IsMouseInside(min, max);

    public static bool IsAnyMouseClickedInside(Vector2 min, Vector2 max)
        => IsMouseInside(min, max)
           && (ImGui.IsMouseClicked(ImGuiMouseButton.Left)
               || ImGui.IsMouseClicked(ImGuiMouseButton.Right)
               || ImGui.IsMouseClicked(ImGuiMouseButton.Middle));
}

