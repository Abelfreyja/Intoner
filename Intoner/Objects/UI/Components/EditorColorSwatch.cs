using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal static class EditorColorSwatch
{
    public static void Draw(ImDrawListPtr drawList, Vector2 min, Vector2 max, Vector4 color, bool isDefault, bool selected = false)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float rounding = 3f * scale;
        Vector4 fill = isDefault ? ThemeColors.ButtonDefault with { W = 0.96f } : color;
        Vector4 border = selected ? ThemeColors.Text : ThemeColors.Border with { W = 0.78f };
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), rounding);
        drawList.AddRect(min, max, ImGui.GetColorU32(border), rounding, ImDrawFlags.None, selected ? 2f * scale : scale);
        if (isDefault)
        {
            drawList.AddLine(new Vector2(min.X + 3f * scale, max.Y - 3f * scale),
                new Vector2(max.X - 3f * scale, min.Y + 3f * scale), ImGui.GetColorU32(color), 1.25f * scale);
        }
    }
}
