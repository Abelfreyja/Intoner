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
        => BeginRow(definition.Label, definition.Description, rowHeight);

    public static void BeginRow(string label, string description, float rowHeight)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
        ImGui.TableNextColumn();
        DrawBody(label, description, rowHeight);
        ImGui.TableNextColumn();
    }

    public static void DrawBody(SettingDefinition definition, float rowHeight)
        => DrawBody(definition.Label, definition.Description, rowHeight);

    public static void DrawBody(string label, string description, float rowHeight)
    {
        float bodyHeight = (ImGui.GetTextLineHeight() * 2f) + ImGui.GetStyle().ItemSpacing.Y;
        float cursorY = ImGui.GetCursorPosY();
        ImGui.SetCursorPosY(cursorY + MathF.Max(0f, (rowHeight - bodyHeight) * 0.5f));

        ImGui.TextUnformatted(label);
        DrawTooltip(description);

        using var wrap = ImRaiiScope.TextWrapPos();
        ImGui.TextDisabled(description);
        DrawTooltip(description);
    }

    public static void DrawDescriptionTooltip(SettingDefinition definition)
        => DrawTooltip(definition.Description);

    public static void DrawTooltip(string text)
    {
        if (!string.IsNullOrWhiteSpace(text) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            IntonerTooltip.Attach(text);
        }
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

