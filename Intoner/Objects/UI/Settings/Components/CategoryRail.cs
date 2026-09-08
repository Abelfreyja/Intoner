using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.UI.Components;
using System.Numerics;
using static Intoner.Objects.UI.Settings.Components.SettingsChrome;

namespace Intoner.Objects.UI.Settings.Components;

internal static class CategoryRail
{
    public static string? Draw(SettingsView view, string? selectedTabId)
    {
        string? nextSelectedTabId = selectedTabId;

        using var rowSpacing = ImRaii.PushStyle(
            ImGuiStyleVar.ItemSpacing,
            new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f));
        for (var index = 0; index < view.Categories.Count; ++index)
        {
            DrawCategory(view.Categories[index], ref nextSelectedTabId, index + 1 < view.Categories.Count);
        }

        return nextSelectedTabId;
    }

    private static void DrawCategory(CategoryResult category, ref string? selectedTabId, bool drawBottomSpacing)
    {
        bool selected = string.Equals(selectedTabId, category.Tab?.Id, StringComparison.Ordinal);
        float rowHeight = Scaled(CategoryRowHeight);
        Vector2 rowMin = ImGui.GetCursorScreenPos();
        Vector2 rowSize = new(Positive(ImGui.GetContentRegionAvail().X), rowHeight);

        if (ImGui.InvisibleButton($"##objectSettingsCategory{category.Tab?.Id ?? "all"}", rowSize))
        {
            selectedTabId = category.Tab?.Id;
        }

        DrawCategoryRow(category, rowMin, rowSize, selected, ImGui.IsItemHovered());
        if (drawBottomSpacing)
        {
            ImGuiHelpers.ScaledDummy(CompactCardSpacingY);
        }
    }

    private static void DrawCategoryRow(CategoryResult category, Vector2 rowMin, Vector2 rowSize, bool selected, bool hovered)
    {
        Vector4 accent = ResolveCategoryColor(category.Tab);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        Vector2 rowMax = rowMin + rowSize;
        var rounding = Scaled(5f);
        Vector4 fill = Vector4.Zero;
        if (selected)
        {
            fill = accent with { W = 0.18f };
        }
        else if (hovered)
        {
            fill = ThemeColors.ButtonDefault with { W = 0.36f };
        }

        if (fill.W > 0f)
        {
            drawList.AddRectFilled(rowMin, rowMax, ImGui.GetColorU32(fill), rounding);
        }

        if (selected)
        {
            Vector2 accentMax = new(rowMin.X + Scaled(4f), rowMax.Y);
            drawList.AddRectFilled(rowMin, accentMax, ImGui.GetColorU32(accent), rounding, ImDrawFlags.RoundCornersLeft);
        }

        string count = category.Result.EntryCount.ToString();
        EditorBadge countBadge = EditorBadge.Label(count, color: selected ? accent : ThemeColors.TextDisabled);
        float centerY = rowMin.Y + ((rowSize.Y - ImGui.GetTextLineHeight()) * 0.5f);
        Vector2 iconPos = new(rowMin.X + Scaled(12f), centerY);
        Vector2 labelPos = new(rowMin.X + Scaled(34f), centerY);
        Vector4 textColor = selected ? ThemeColors.Text : ThemeColors.TextDisabled;

        EditorIcon.DrawCentered(drawList, ResolveCategoryIcon(category.Tab), iconPos, iconPos + new Vector2(Scaled(16f), ImGui.GetTextLineHeight()), accent);
        float badgeLeft = EditorBadgeRenderer.DrawRightAligned(drawList, null, countBadge, rowMax.X - Scaled(8f),
            rowMin.Y + (rowSize.Y - EditorBadgeRenderer.Height) * 0.5f, selected, 1f, panelSurface: true);
        float labelWidth = MathF.Max(0f, badgeLeft - Scaled(4f) - labelPos.X);
        EditorTextUtility.ClippedText label = EditorTextUtility.ClipTextToWidthResult(category.Label, labelWidth);
        drawList.AddText(labelPos, ImGui.GetColorU32(textColor), label.Text);
        EditorTextUtility.AttachTooltipIfClipped(labelPos, new Vector2(labelWidth, ImGui.GetTextLineHeight()), category.Label, label.IsClipped);
    }
}

