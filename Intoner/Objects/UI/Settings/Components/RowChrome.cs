using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.UI;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI.Settings.Components;

internal static class RowChrome
{
    private const float SettingRowMinHeight = 50f;

    public static void BeginRow(SettingDefinition definition, float rowHeight)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
        ImGui.TableNextColumn();
        DrawBody(definition, rowHeight);
        ImGui.TableNextColumn();
    }

    public static void DrawBody(SettingDefinition definition, float rowHeight)
    {
        float bodyHeight = (ImGui.GetTextLineHeight() * 2f) + ImGui.GetStyle().ItemSpacing.Y;
        float cursorY = ImGui.GetCursorPosY();
        ImGui.SetCursorPosY(cursorY + MathF.Max(0f, (rowHeight - bodyHeight) * 0.5f));

        ImGui.TextUnformatted(definition.Label);
        DrawDescriptionTooltip(definition);

        using var wrap = ImRaiiScope.TextWrapPos();
        ImGui.TextDisabled(definition.Description);
        DrawDescriptionTooltip(definition);
    }

    public static void DrawDescriptionTooltip(SettingDefinition definition)
        => DrawTooltip(definition.Description);

    public static void DrawTooltip(string text)
    {
        if (!string.IsNullOrWhiteSpace(text) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            UiSharedService.AttachToolTip(text);
        }
    }

    public static void DrawStatusBadge(string text, Vector4 color, Vector2 badgeSize, Vector2 padding, float rounding)
    {
        Vector2 badgeMin = ImGui.GetCursorScreenPos();
        Vector2 badgeMax = badgeMin + badgeSize;
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(badgeMin, badgeMax, ImGui.GetColorU32(color with { W = 0.14f }), rounding);
        drawList.AddRect(badgeMin, badgeMax, ImGui.GetColorU32(color with { W = 0.32f }), rounding, ImDrawFlags.None, 1f * ImGuiHelpers.GlobalScale);
        drawList.AddText(badgeMin + padding, ImGui.GetColorU32(color), text);
        ImGui.Dummy(badgeSize);
    }

    public static float ResolveRowHeight(float controlHeight)
        => MathF.Max(SettingRowMinHeight * ImGuiHelpers.GlobalScale, controlHeight + (14f * ImGuiHelpers.GlobalScale));

    public static float AvailableControlWidth()
        => MathF.Max(1f, ImGui.GetContentRegionAvail().X);

    public static float ResolveControlWidth(float availableWidth, SettingRowLayout layout)
    {
        float targetWidth = SettingsChrome.Scaled(layout.ControlColumnWidth ?? SettingsChrome.DefaultControlWidth);
        return MathF.Max(1f, MathF.Min(availableWidth, targetWidth));
    }

    public static void AlignControl(float rowHeight, float controlHeight, float controlWidth, bool alignRight = false)
    {
        Vector2 cursor = ImGui.GetCursorPos();
        float offsetX = alignRight
            ? MathF.Max(0f, ImGui.GetContentRegionAvail().X - controlWidth)
            : 0f;

        ImGui.SetCursorPos(new Vector2(
            cursor.X + offsetX,
            cursor.Y + MathF.Max(0f, (rowHeight - controlHeight) * 0.5f)));
    }
}

