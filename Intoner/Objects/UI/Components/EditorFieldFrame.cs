using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal static class EditorFieldFrame
{
    public static void DrawFill(ImDrawListPtr drawList, Vector2 min, Vector2 max)
    {
        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(ThemeColors.ButtonDefault with { W = 0.48f }),
            EditorLayout.Scaled(5f));
    }

    public static void DrawBorder(ImDrawListPtr drawList, Vector2 min, Vector2 max, Vector4 accent, bool active, bool hovered)
    {
        Vector4 border = ThemeColors.Border with { W = hovered ? 0.54f : 0.30f };

        if (active)
        {
            border = accent with { W = 0.52f };
        }

        drawList.AddRect(min, max, ImGui.GetColorU32(border), EditorLayout.Scaled(5f), ImDrawFlags.None, EditorLayout.Scaled(1f));
    }
}
