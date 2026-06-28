using Dalamud.Bindings.ImGui;
using System.Numerics;
using static Intoner.Objects.UI.Settings.Components.SettingsChrome;

namespace Intoner.Objects.UI.Settings.Components;

internal static class RangeLabels
{
    public static void Draw(string minimumText, string maximumText, float width, bool enabled)
    {
        Vector2 min = ImGui.GetCursorScreenPos();
        Vector4 color = enabled
            ? EditorColors.TextDisabled with { W = 0.74f }
            : EditorColors.TextDisabled with { W = 0.42f };
        Vector2 minimumTextSize = ImGui.CalcTextSize(minimumText);
        Vector2 maximumTextSize = ImGui.CalcTextSize(maximumText);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        drawList.AddText(min, ImGui.GetColorU32(color), minimumText);
        if (minimumTextSize.X + maximumTextSize.X + Scaled(12f) <= width)
        {
            drawList.AddText(
                new Vector2(min.X + MathF.Max(0f, width - maximumTextSize.X), min.Y),
                ImGui.GetColorU32(color),
                maximumText);
        }

        ImGui.Dummy(new Vector2(width, ImGui.GetTextLineHeight()));
    }
}

