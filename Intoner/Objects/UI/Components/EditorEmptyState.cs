using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal static class EditorEmptyState
{
    public static void Draw(string text, float height)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float width = MathF.Max(1f, ImGui.GetContentRegionAvail().X - scale);
        Vector2 start = ImGui.GetCursorScreenPos();
        float displayHeight = MathF.Max(120f * scale, height);
        EditorIcon.Metrics iconMetrics = EditorIcon.Measure(FontAwesomeIcon.Search);

        float wrapWidth = MathF.Max(1f, width - (36f * scale));
        Vector2 textSize = ImGui.CalcTextSize(text, wrapWidth: wrapWidth);
        float contentGap = 12f * scale;
        float contentHeight = iconMetrics.Size.Y + contentGap + textSize.Y;
        float contentY = start.Y + MathF.Max(0f, (displayHeight - contentHeight) * 0.5f);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        ImGui.Dummy(new Vector2(width, displayHeight));

        EditorIcon.Draw(
            drawList,
            FontAwesomeIcon.Search,
            iconMetrics,
            new Vector2(start.X + ((width - iconMetrics.Size.X) * 0.5f), contentY),
            ThemeColors.AccentPrimary with { W = 0.8f });
        drawList.AddText(
            ImGui.GetFont(),
            ImGui.GetFontSize(),
            new Vector2(
                start.X + MathF.Max(0f, (width - MathF.Min(wrapWidth, textSize.X)) * 0.5f),
                contentY + iconMetrics.Size.Y + contentGap),
            ImGui.GetColorU32(ThemeColors.TextDisabled with { W = ThemeColors.Text.W }),
            text,
            wrapWidth);
    }
}
