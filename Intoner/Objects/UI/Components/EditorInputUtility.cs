using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal static class EditorInputUtility
{
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

