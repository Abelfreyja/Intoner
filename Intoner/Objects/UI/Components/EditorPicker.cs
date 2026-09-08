using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal static class EditorPicker
{
    private const float ControlHeight = 44f;
    private const float HorizontalPadding = 10f;
    private const float IconColumnWidth = 18f;
    private const float ColumnGap = 8f;
    private const float TextGap = 1f;

    public static float Height => Scaled(ControlHeight);

    public static bool DrawDetailed(
        string id,
        FontAwesomeIcon icon,
        string title,
        string detail,
        Vector4 accent,
        float width = 0f)
    {
        EditorIcon.Metrics iconMetrics = EditorIcon.Measure(icon);
        EditorIcon.Metrics chevronMetrics = EditorIcon.Measure(FontAwesomeIcon.ChevronDown, 0.82f);
        float resolvedWidth = ResolveWidth(title, detail, chevronMetrics.Size.X, width);
        bool clicked = ImGui.InvisibleButton($"##editorPicker:{id}", new Vector2(resolvedWidth, Height));
        bool hovered = ImGui.IsItemHovered();
        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();

        DrawFrame(min, max, accent, hovered, ImGui.IsItemActive());
        DrawContent(icon, title, detail, accent, min, max, iconMetrics, chevronMetrics);

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        return clicked;
    }

    private static float ResolveWidth(
        string title,
        string detail,
        float chevronWidth,
        float requestedWidth)
    {
        if (requestedWidth > 0f)
        {
            return requestedWidth;
        }

        float textWidth = MathF.Max(ImGui.CalcTextSize(title).X, ImGui.CalcTextSize(detail).X);
        return Scaled(HorizontalPadding * 2f)
             + Scaled(IconColumnWidth)
             + chevronWidth
             + Scaled(ColumnGap * 2f)
             + textWidth;
    }

    private static void DrawFrame(Vector2 min, Vector2 max, Vector4 accent, bool hovered, bool active)
    {
        float accentMix = 0.04f;
        float fillAlpha = 0.54f;
        if (active)
        {
            accentMix = 0.14f;
            fillAlpha = 0.68f;
        }
        else if (hovered)
        {
            accentMix = 0.08f;
            fillAlpha = 0.60f;
        }

        Vector4 baseFill = ThemeColors.ButtonDefault;
        Vector4 fill = Vector4.Lerp(baseFill, accent, accentMix)
            with { W = fillAlpha };
        Vector4 border = ThemeColors.Border with { W = 0.62f };
        if (active)
        {
            border = accent with { W = 0.56f };
        }
        else if (hovered)
        {
            border = accent with { W = 0.44f };
        }

        float rounding = Scaled(5f);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), rounding);
        drawList.AddRect(min, max, ImGui.GetColorU32(border), rounding, ImDrawFlags.None, Scaled(1f));
    }

    private static void DrawContent(
        FontAwesomeIcon icon,
        string title,
        string detail,
        Vector4 accent,
        Vector2 min,
        Vector2 max,
        EditorIcon.Metrics iconMetrics,
        EditorIcon.Metrics chevronMetrics)
    {
        float padding = Scaled(HorizontalPadding);
        float iconColumnWidth = Scaled(IconColumnWidth);
        float gap = Scaled(ColumnGap);
        float iconX = min.X + padding + ((iconColumnWidth - iconMetrics.Size.X) * 0.5f);
        float textX = min.X + padding + iconColumnWidth + gap;
        float chevronX = max.X - padding - chevronMetrics.Size.X;
        float textWidth = MathF.Max(1f, chevronX - gap - textX);
        float lineHeight = ImGui.GetTextLineHeight();
        float blockHeight = (lineHeight * 2f) + Scaled(TextGap);
        float titleY = min.Y + ((max.Y - min.Y - blockHeight) * 0.5f);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        EditorIcon.Draw(
            drawList,
            icon,
            iconMetrics,
            new Vector2(
                iconX,
                min.Y + ((max.Y - min.Y - iconMetrics.Size.Y) * 0.5f)),
            accent);
        drawList.AddText(
            new Vector2(textX, titleY),
            ImGui.GetColorU32(ThemeColors.Text),
            EditorTextUtility.ClipTextToWidth(title, textWidth));
        drawList.AddText(
            new Vector2(textX, titleY + lineHeight + Scaled(TextGap)),
            ImGui.GetColorU32(ThemeColors.TextDisabled),
            EditorTextUtility.ClipTextToWidth(detail, textWidth));
        EditorIcon.Draw(
            drawList,
            FontAwesomeIcon.ChevronDown,
            chevronMetrics,
            new Vector2(chevronX, min.Y + ((max.Y - min.Y - chevronMetrics.Size.Y) * 0.5f)),
            accent with { W = 0.78f });
    }

    private static float Scaled(float value)
        => value * ImGuiHelpers.GlobalScale;
}
