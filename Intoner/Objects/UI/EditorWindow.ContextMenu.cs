using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private const ImGuiWindowFlags ContextMenuPopupFlags =
        ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

    private readonly record struct ContextMenuRowResult(bool Activated, bool Hovered, Vector2 Min, Vector2 Max);

    private static bool DrawContextMenuItem(FontAwesomeIcon icon, string label, bool selected = false, bool enabled = true)
    {
        var row = DrawContextMenuRow(label, icon, label, enabled, selected, hasSubMenu: false);
        if (!row.Activated)
        {
            return false;
        }

        ImGui.CloseCurrentPopup();
        return true;
    }

    private static bool DrawFolderSelectionContextMenu(
        IReadOnlyList<string> placedFolders,
        string selectedFolderPath,
        Action<string> onFolderSelected,
        bool showEmptyHint = true)
    {
        if (DrawContextMenuItem(FontAwesomeIcon.TimesCircle, "Ungrouped", string.IsNullOrWhiteSpace(selectedFolderPath)))
        {
            onFolderSelected(string.Empty);
            return true;
        }

        foreach (var folderPath in placedFolders)
        {
            var folderSelected = string.Equals(selectedFolderPath, folderPath, StringComparison.OrdinalIgnoreCase);
            if (!DrawContextMenuItem(FontAwesomeIcon.Folder, ResolveFolderDisplayLabel(folderPath), folderSelected))
            {
                continue;
            }

            onFolderSelected(folderPath);
            return true;
        }

        if (showEmptyHint && placedFolders.Count == 0)
        {
            ImGui.TextDisabled("No folders available");
        }

        return false;
    }

    private static ImRaiiScope.PopupScope BeginContextSubMenu(string id, FontAwesomeIcon icon, string label, bool enabled = true)
    {
        var parentWindowPos = ImGui.GetWindowPos();
        var parentWindowSize = ImGui.GetWindowSize();
        var row = DrawContextMenuRow(id, icon, label, enabled, selected: false, hasSubMenu: true);
        var popupId = $"##contextSubMenu:{id}";
        if (enabled && row.Activated)
        {
            ImGui.OpenPopup(popupId);
        }

        var scale = ImGuiHelpers.GlobalScale;
        var style = ImGui.GetStyle();
        ImGui.SetNextWindowPos(
            new Vector2(
                parentWindowPos.X + parentWindowSize.X - (1f * scale),
                row.Min.Y - style.WindowPadding.Y),
            ImGuiCond.Always);
        return ImRaiiScope.Popup(popupId, ContextMenuPopupFlags);
    }

    private static ContextMenuRowResult DrawContextMenuRow(
        string id,
        FontAwesomeIcon icon,
        string label,
        bool enabled,
        bool selected,
        bool hasSubMenu)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var iconText = icon.ToIconString();
        var trailingIcon = hasSubMenu
            ? FontAwesomeIcon.ChevronRight.ToIconString()
            : selected
                ? FontAwesomeIcon.Check.ToIconString()
                : string.Empty;

        var labelSize = ImGui.CalcTextSize(label);
        var iconSize = MeasureContextMenuIcon(iconText);
        var trailingSize = string.IsNullOrEmpty(trailingIcon)
            ? Vector2.Zero
            : MeasureContextMenuIcon(trailingIcon);

        var rowHeight = MathF.Max(
            22f * scale,
            MathF.Max(labelSize.Y, MathF.Max(iconSize.Y, trailingSize.Y)) + (8f * scale));
        var rowWidth = MathF.Max(
            150f * scale,
            (12f * scale)
            + (iconSize.X + (10f * scale))
            + labelSize.X
            + (trailingSize.X > 0f ? (12f * scale) + trailingSize.X : 0f)
            + (12f * scale));

        var activated = false;
        var hovered = false;
        using (ImRaii.Disabled(!enabled))
        {
            activated = ImGui.InvisibleButton($"##{id}", new Vector2(rowWidth, rowHeight));
            hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        }

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var style = ImGui.GetStyle();
        var background = hovered
            ? style.Colors[(int)ImGuiCol.HeaderHovered]
            : Vector4.Zero;
        if (activated && enabled)
        {
            background = style.Colors[(int)ImGuiCol.HeaderActive];
        }

        if (background.W > 0f)
        {
            drawList.AddRectFilled(
                min,
                max,
                ImGui.GetColorU32(background),
                4f * scale);
        }

        var iconColor = enabled
            ? hovered || selected
                ? EditorColors.AccentBlue
                : EditorColors.Text
            : EditorColors.TextDisabled;
        var labelColor = enabled
            ? EditorColors.Text
            : EditorColors.TextDisabled;
        var iconY = min.Y + ((rowHeight - iconSize.Y) * 0.5f);
        var labelY = min.Y + ((rowHeight - labelSize.Y) * 0.5f);

        var iconPos = new Vector2(
            min.X + (7f * scale),
            iconY);
        var labelPos = new Vector2(
            min.X + (12f * scale) + iconSize.X + (10f * scale),
            labelY);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            drawList.AddText(
                iconPos,
                ImGui.GetColorU32(iconColor),
                iconText);
        }

        drawList.AddText(labelPos, ImGui.GetColorU32(labelColor), label);

        if (!string.IsNullOrEmpty(trailingIcon))
        {
            var trailingY = min.Y + ((rowHeight - trailingSize.Y) * 0.5f);
            var trailingPos = new Vector2(
                max.X - (8f * scale) - trailingSize.X,
                trailingY);
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                drawList.AddText(
                    trailingPos,
                    ImGui.GetColorU32(EditorColors.TextDisabled),
                    trailingIcon);
            }
        }

        return new ContextMenuRowResult(activated && enabled, hovered, min, max);
    }

    private static Vector2 MeasureContextMenuIcon(string iconText)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            return ImGui.CalcTextSize(iconText);
        }
    }
}

