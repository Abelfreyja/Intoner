using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;
using static Intoner.Objects.UI.Settings.Components.SettingsChrome;

namespace Intoner.Objects.UI.Settings.Components;

internal static class CategoryRail
{
    public static SettingsTab? Draw(SettingsView view, SettingsTab? selectedTab)
    {
        SettingsTab? nextSelectedTab = selectedTab;

        using var rowSpacing = ImRaii.PushStyle(
            ImGuiStyleVar.ItemSpacing,
            new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f));
        for (var index = 0; index < view.Categories.Count; ++index)
        {
            DrawCategory(view.Categories[index], ref nextSelectedTab, index + 1 < view.Categories.Count);
        }

        return nextSelectedTab;
    }

    private static void DrawCategory(CategoryResult category, ref SettingsTab? selectedTab, bool drawBottomSpacing)
    {
        bool selected = selectedTab == category.Tab;
        float rowHeight = Scaled(CategoryRowHeight);
        Vector2 rowMin = ImGui.GetCursorScreenPos();
        Vector2 rowSize = new(Positive(ImGui.GetContentRegionAvail().X), rowHeight);

        if (ImGui.InvisibleButton($"##objectSettingsCategory{category.Label}", rowSize))
        {
            selectedTab = category.Tab;
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
        Vector4 fill = selected
            ? accent with { W = 0.18f }
            : hovered
                ? EditorColors.ButtonDefault with { W = 0.36f }
                : Vector4.Zero;

        if (fill.W > 0f)
        {
            drawList.AddRectFilled(rowMin, rowMax, ImGui.GetColorU32(fill), rounding);
        }

        if (selected)
        {
            Vector2 accentMax = new(rowMin.X + Scaled(4f), rowMax.Y);
            drawList.AddRectFilled(rowMin, accentMax, ImGui.GetColorU32(accent), rounding, ImDrawFlags.RoundCornersLeft);
        }

        string icon = ResolveCategoryIcon(category.Tab).ToIconString();
        string count = category.Result.EntryCount.ToString();
        Vector2 countSize = ImGui.CalcTextSize(count);
        Vector2 countPadding = new(7f * ImGuiHelpers.GlobalScale, 1f * ImGuiHelpers.GlobalScale);
        Vector2 countBadgeSize = countSize + (countPadding * 2f);
        float centerY = rowMin.Y + ((rowSize.Y - ImGui.GetTextLineHeight()) * 0.5f);
        Vector2 iconPos = new(rowMin.X + Scaled(12f), centerY);
        Vector2 labelPos = new(rowMin.X + Scaled(34f), centerY);
        Vector2 countBadgeMin = new(rowMax.X - countBadgeSize.X - Scaled(8f), rowMin.Y + ((rowSize.Y - countBadgeSize.Y) * 0.5f));
        Vector4 textColor = selected ? EditorColors.Text : EditorColors.TextDisabled;

        drawList.AddText(UiBuilder.IconFont, ImGui.GetFontSize(), iconPos, ImGui.GetColorU32(accent), icon);
        drawList.AddText(labelPos, ImGui.GetColorU32(textColor), category.Label);
        DrawBadge(drawList, countBadgeMin, countBadgeSize, count, countPadding, selected ? accent : EditorColors.TextDisabled);
    }
}

