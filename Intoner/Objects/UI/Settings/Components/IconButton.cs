using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.UI;
using System.Numerics;
using static Intoner.Objects.UI.Settings.Components.SettingsChrome;

namespace Intoner.Objects.UI.Settings.Components;

internal static class IconButton
{
    public static bool DrawSquare(string id, FontAwesomeIcon icon, float edge, Vector4 accent, bool enabled)
    {
        bool clicked;
        using (ImRaii.Disabled(!enabled))
        {
            clicked = ImGui.InvisibleButton(id, new Vector2(edge));
        }

        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();
        bool hovered = enabled && ImGui.IsItemHovered();
        Vector4 fill = hovered
            ? accent with { W = 0.24f }
            : EditorColors.ButtonDefault with { W = enabled ? 0.42f : 0.20f };
        Vector4 border = hovered
            ? accent with { W = 0.46f }
            : EditorColors.Border with { W = enabled ? 0.28f : 0.14f };
        Vector4 iconColor = enabled
            ? EditorColors.Text with { W = hovered ? 0.98f : 0.82f }
            : EditorColors.TextDisabled with { W = 0.48f };
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        var rounding = edge * 0.35f;

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), rounding);
        drawList.AddRect(min, max, ImGui.GetColorU32(border), rounding, ImDrawFlags.None, Scaled(1f));
        DrawCenteredIcon(drawList, min, max, icon, iconColor);
        return clicked;
    }
}

