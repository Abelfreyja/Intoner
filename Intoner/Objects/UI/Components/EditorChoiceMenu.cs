using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal static class EditorChoiceMenu
{
    private const float RowHeight = 46f;
    private const float HorizontalPadding = 8f;
    private const float IconColumnWidth = 20f;
    private const float ColumnGap = 8f;
    private const float TextGap = 2f;

    public static bool DrawOption(
        string id,
        FontAwesomeIcon icon,
        string label,
        string detail,
        bool selected)
        => DrawOption(id, icon, label, detail, null, selected);

    public static bool DrawOption(
        string id,
        FontAwesomeIcon icon,
        string label,
        IReadOnlyList<EditorBadge> badges,
        bool selected)
        => DrawOption(id, icon, label, string.Empty, badges, selected);

    private static bool DrawOption(
        string id,
        FontAwesomeIcon icon,
        string label,
        string detail,
        IReadOnlyList<EditorBadge>? badges,
        bool selected)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float width = ImGui.GetContentRegionAvail().X;
        bool clicked = ImGui.InvisibleButton($"##editorChoice:{id}", new Vector2(width, RowHeight * scale));
        bool hovered = ImGui.IsItemHovered();
        bool active = ImGui.IsItemActive();
        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        Vector4 fill = selected
            ? ThemeColors.AccentPrimary with { W = 0.18f }
            : Vector4.Zero;
        if (hovered)
        {
            fill = ThemeColors.AccentPrimary with { W = active ? 0.30f : 0.24f };
        }

        if (fill.W > 0f)
        {
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), 4f * scale);
        }

        EditorIcon.Metrics iconMetrics = EditorIcon.Measure(icon);
        EditorIcon.Metrics checkMetrics = EditorIcon.Measure(FontAwesomeIcon.Check);

        float padding = HorizontalPadding * scale;
        float iconColumn = IconColumnWidth * scale;
        float gap = ColumnGap * scale;
        float textX = min.X + padding + iconColumn + gap;
        float trailingWidth = selected ? iconColumn + gap : 0f;
        float textWidth = MathF.Max(1f, max.X - padding - trailingWidth - textX);
        float lineHeight = ImGui.GetTextLineHeight();
        float detailHeight = badges is { Count: > 0 }
            ? EditorBadgeRenderer.Height
            : lineHeight;
        float blockHeight = lineHeight + detailHeight + (TextGap * scale);
        float titleY = min.Y + ((max.Y - min.Y - blockHeight) * 0.5f);
        Vector2 iconPosition = new(
            min.X + padding + ((iconColumn - iconMetrics.Size.X) * 0.5f),
            min.Y + ((max.Y - min.Y - iconMetrics.Size.Y) * 0.5f));

        EditorIcon.Draw(
            drawList,
            icon,
            iconMetrics,
            iconPosition,
            ThemeColors.AccentPrimary);
        drawList.AddText(
            new Vector2(textX, titleY),
            ImGui.GetColorU32(ThemeColors.Text),
            EditorTextUtility.ClipTextToWidth(label, textWidth));
        float detailY = titleY + lineHeight + (TextGap * scale);
        if (badges is { Count: > 0 })
        {
            EditorBadgeRenderer.DrawAt(
                drawList,
                badges,
                textX,
                detailY,
                maxRight: textX + textWidth);
        }
        else
        {
            drawList.AddText(
                new Vector2(textX, detailY),
                ImGui.GetColorU32(ThemeColors.TextDisabled),
                EditorTextUtility.ClipTextToWidth(detail, textWidth));
        }

        if (selected)
        {
            Vector2 checkPosition = new(
                max.X - padding - ((iconColumn + checkMetrics.Size.X) * 0.5f),
                min.Y + ((max.Y - min.Y - checkMetrics.Size.Y) * 0.5f));
            EditorIcon.Draw(
                drawList,
                FontAwesomeIcon.Check,
                checkMetrics,
                checkPosition,
                ThemeColors.AccentGreen);
        }

        if (clicked)
        {
            ImGui.CloseCurrentPopup();
        }

        return clicked;
    }
}
