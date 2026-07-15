using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.UI.Components;
using Intoner.Objects.Utils;
using Intoner.UI;
using System.Numerics;
using static Intoner.Objects.UI.Components.EditorCard;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private static readonly Vector4 AxisXColor = new(0.95f, 0.42f, 0.42f, 0.95f);
    private static readonly Vector4 AxisYColor = new(0.46f, 0.84f, 0.52f, 0.95f);
    private static readonly Vector4 AxisZColor = new(0.46f, 0.66f, 1.00f, 0.95f);

    private static void DrawCreateHero(FontAwesomeIcon icon, string title, string description)
    {
        var accent = EditorColors.AccentPurple;
        DrawPanelCard(
            $"create-hero-{title}",
            EditorColors.ButtonDefault with { W = 0.30f },
            accent with { W = 0.28f },
            Scaled(8f),
            ResolveObjectListCardPadding(),
            () =>
            {
                DrawIconTitleBlock(icon, title, description, accent);
            });
    }

    private static ImRaiiScope.TableScope CompactSettingsTable(string id)
    {
        const ImGuiTableFlags flags =
            ImGuiTableFlags.SizingStretchProp
          | ImGuiTableFlags.RowBg
          | ImGuiTableFlags.BordersInnerV
          | ImGuiTableFlags.BordersInnerH
          | ImGuiTableFlags.NoPadOuterX
          | ImGuiTableFlags.NoSavedSettings;

        var table = ImRaiiScope.Table($"##{id}", 2, flags, ScaledVector(8f, 3f));
        if (!table)
        {
            return table;
        }

        ImGui.TableSetupColumn("Setting", ImGuiTableColumnFlags.WidthFixed, Scaled(156f));
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
        return table;
    }

    private static void DrawCompactSettingsLabelCell(string title, float actionWidth = 0f, Action? drawActions = null)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.None, ImGui.GetFrameHeight() + Scaled(2f));
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        DrawCompactSettingsLabel(title, actionWidth, drawActions);

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1f);
    }

    private static void DrawCompactSettingsLabel(string title, float actionWidth, Action? drawActions)
    {
        if (drawActions is null || actionWidth <= 0f)
        {
            ImGui.TextUnformatted(title);
            return;
        }

        float startX = ImGui.GetCursorPosX();
        float availableWidth = ImGui.GetContentRegionAvail().X;
        ImGui.TextUnformatted(title);

        float actionX = startX + MathF.Max(0f, availableWidth - actionWidth);
        ImGui.SameLine();
        ImGui.SetCursorPosX(actionX);
        drawActions();
    }

    private static bool DrawCheckboxRow(string id, string title, ref bool value)
    {
        DrawCompactSettingsLabelCell(title);
        return ImGui.Checkbox($"##{id}", ref value);
    }

    private static bool DrawSliderFloatRow(string id, string title, ref float value, float min, float max, string format)
    {
        DrawCompactSettingsLabelCell(title);
        return ImGui.SliderFloat($"##{id}", ref value, min, max, format);
    }

    private static bool DrawDragFloatRow(string id, string title, ref float value, float speed, float min, float max, string format)
    {
        DrawCompactSettingsLabelCell(title);
        return ImGui.DragFloat($"##{id}", ref value, speed, min, max, format);
    }

    private static bool DrawDragFloat3Row(
        string id,
        string title,
        ref Vector3 value,
        float speed,
        float min,
        float max,
        string format,
        float actionWidth = 0f,
        Action? drawActions = null)
    {
        DrawCompactSettingsLabelCell(title, actionWidth, drawActions);
        return DrawDragFloat3Inputs(id, ref value, speed, min, max, format);
    }

    private static bool DrawDragFloat3Inputs(string id, ref Vector3 value, float speed, float min, float max, string format)
    {
        var x = value.X;
        var y = value.Y;
        var z = value.Z;
        var changed = false;
        var axisGap = Scaled(6f);
        var axisValueGap = Scaled(3f);
        var axisLabelWidth = MathF.Ceiling(ImGui.CalcTextSize("X").X);
        var availableWidth = Positive(ImGui.GetContentRegionAvail().X);
        var inputWidth = Positive((availableWidth - (axisLabelWidth * 3f) - (axisGap * 2f) - (axisValueGap * 3f)) / 3f);

        DrawAxisFloatInline($"{id}_x", "X", AxisXColor, ref x, speed, min, max, format, inputWidth, 0f, axisValueGap, ref changed);
        DrawAxisFloatInline($"{id}_y", "Y", AxisYColor, ref y, speed, min, max, format, inputWidth, axisGap, axisValueGap, ref changed);
        DrawAxisFloatInline($"{id}_z", "Z", AxisZColor, ref z, speed, min, max, format, inputWidth, axisGap, axisValueGap, ref changed);

        if (!changed)
        {
            return false;
        }

        value = new Vector3(x, y, z);
        return true;
    }

    private static void DrawAxisFloatInline(string id, string axis, Vector4 color, ref float value, float speed, float min, float max, string format, float inputWidth, float leadingGap, float valueGap, ref bool changed)
    {
        if (leadingGap > 0f)
        {
            ImGui.SameLine(0f, leadingGap);
        }

        ImGui.AlignTextToFramePadding();
        using (ImRaii.PushColor(ImGuiCol.Text, color))
        {
            ImGui.TextUnformatted(axis);
        }

        ImGui.SameLine(0f, valueGap);
        ImGui.SetNextItemWidth(inputWidth);
        using var border = ImRaii.PushColor(ImGuiCol.Border, color with { W = 0.90f });
        using var borderSize = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, Scaled(1f));
        if (ImGui.DragFloat($"##{id}", ref value, speed, min, max, format))
        {
            changed = true;
        }
    }

    private static bool DrawEnumRow<T>(string id, string title, T current, Func<T, string> labelFunc, ref T target) where T : struct, Enum
    {
        DrawCompactSettingsLabelCell(title);
        using var combo = ImRaii.Combo($"##{id}", labelFunc(current));
        if (!combo)
        {
            return false;
        }

        var changed = false;
        foreach (var value in Enum.GetValues<T>())
        {
            var selected = EqualityComparer<T>.Default.Equals(target, value);
            if (ImGui.Selectable(labelFunc(value), selected))
            {
                target = value;
                changed = true;
            }

            if (selected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }
        return changed;
    }

    private void DrawBottomAlignedCreateActionHost(string id, float hostHeight, Action content)
    {
        var style = ImGui.GetStyle();
        using var zeroVerticalPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(style.WindowPadding.X, 0f));
        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
        using var child = ImRaii.Child(
            $"##{id}",
            new Vector2(0f, Positive(hostHeight)),
            false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground);
        if (!child)
        {
            return;
        }

        content();
        var bottomInset = ResolveCreateActionHostBottomInset();
        if (bottomInset > 0f)
        {
            ImGui.Dummy(new Vector2(0f, bottomInset));
        }

        MarkCurrentWindowAsEditorOverlayTarget();
    }

    private void DrawScrollableCreateSettingsCard(string id, float height, Action content)
        => DrawScrollableCreateCard(id, height, null, content, EditorColors.AccentPurple);

    private void DrawScrollableCreateSectionCard(string id, FontAwesomeIcon icon, string title, string description, float height, Action content, Vector4? accentOverride = null)
    {
        var accent = accentOverride ?? EditorColors.AccentPurple;
        DrawScrollableCreateCard(
            id,
            height,
            () =>
            {
                DrawIconTitleBlock(icon, title, description, accent, wrapSubtitle: true);
                ImGuiHelpers.ScaledDummy(4f);
            },
            content,
            accent);
    }

    private void DrawScrollableCreateCard(string id, float height, Action? drawHeader, Action content, Vector4 accent)
    {
        var background = EditorColors.ButtonDefault with { W = 0.24f };
        var rounding = Scaled(8f);
        var padding = ResolveObjectListCardPadding();

        DrawPanelCard(
            id,
            background,
            accent with { W = 0.18f },
            rounding,
            padding,
            height,
            () =>
            {
                var contentStartY = ImGui.GetCursorPosY();
                drawHeader?.Invoke();
                var innerHeight = ResolveScrollableCardInnerHeight(height, padding, contentStartY);

                using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
                _scrollableCreateCardPreviewHovered = false;
                using var child = ObjectScrollList.Begin(
                    $"##{id}_scroll",
                    new Vector2(0f, innerHeight),
                    CreateOverlayScrollPanelOptions(background, rounding, accent),
                    false,
                    ImGuiWindowFlags.NoScrollWithMouse);
                if (child)
                {
                    content();

                    var childHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
                    var wheelDelta = ImGui.GetIO().MouseWheel;
                    if (childHovered && !_scrollableCreateCardPreviewHovered && MathF.Abs(wheelDelta) > 0f)
                    {
                        var step = ImGui.GetTextLineHeightWithSpacing() * 4f;
                        var nextScroll = ImGui.GetScrollY() - (wheelDelta * step);
                        ImGui.SetScrollY(nextScroll);
                    }
                }
            });
    }

    private static float MeasureCreateActionCardHeight()
    {
        var paddingY = Scaled(8f);
        var wrapperSpacingY = ImGui.GetStyle().ItemSpacing.Y * 2f;
        return (paddingY * 2f) + ResolveCreateActionButtonHeight() + wrapperSpacingY;
    }

    private static float MeasureCreateActionHostHeight()
    {
        return MeasureCreateActionCardHeight() + ResolveCreateActionHostBottomInset();
    }

    private static float ResolveCreateActionHostBottomInset()
    {
        return ImGui.GetStyle().ItemSpacing.Y;
    }

    private static void DrawCreateActionCard(
        string id,
        string label,
        string description,
        IReadOnlyList<string> placedFolders,
        string selectedFolderPath,
        Action onClick,
        Action<string> onFolderChanged)
    {
        DrawPanelCard(
            $"create-action-{id}",
            EditorColors.AccentPurple with { W = 0.14f },
            EditorColors.AccentPurple with { W = 0.32f },
            Scaled(8f),
            ResolveObjectListCardPadding(),
            () =>
            {
                var buttonGap = Scaled(8f);
                var availableWidth = Positive(ImGui.GetContentRegionAvail().X);
                var buttonHeight = ResolveCreateActionButtonHeight();
                var folderButtonWidth = buttonHeight;
                var buttonWidth = Positive(availableWidth - folderButtonWidth - buttonGap);
                var folderPopupId = $"##create-action-folder-popup:{id}";
                var placementLabel = BuildCreatePlacementLabel(label, selectedFolderPath);
                var folderButtonTooltip = BuildCreatePlacementFolderTooltip(selectedFolderPath, placedFolders.Count > 0);
                var folderSelected = !string.IsNullOrEmpty(selectedFolderPath);

                if (DrawCreatePrimaryButton($"createAction:{id}:place", FontAwesomeIcon.Play, placementLabel, new Vector2(buttonWidth, buttonHeight)))
                {
                    onClick();
                }

                if (ImGui.IsItemHovered())
                {
                    UiSharedService.AttachToolTip(description);
                }

                ImGui.SameLine(0f, buttonGap);
                bool folderButtonClicked = DrawAccentToggleIconButton(
                        $"createAction:{id}:folder",
                        folderSelected ? FontAwesomeIcon.FolderOpen : FontAwesomeIcon.Folder,
                        new Vector2(folderButtonWidth, buttonHeight),
                        folderSelected);
                bool folderButtonHovered = ImGui.IsItemHovered();

                using (EditorContextMenu.PopupScope popup = EditorContextMenu.BeginDropdownForLastItem(folderPopupId, folderButtonClicked))
                {
                    if (popup)
                    {
                        DrawFolderSelectionMenu(placedFolders, selectedFolderPath, onFolderChanged);
                    }
                }

                if (folderButtonHovered)
                {
                    UiSharedService.AttachToolTip(folderButtonTooltip);
                }
            });
    }

    private static float ResolveCreateActionButtonHeight()
    {
        return Scaled(40f);
    }

    private static bool DrawCreatePrimaryButton(string id, FontAwesomeIcon icon, string label, Vector2 size)
    {
        var accent = EditorColors.AccentOrange;
        var fill = accent with { W = 0.24f };
        var hoverFill = accent with { W = 0.34f };
        var activeFill = accent with { W = 0.28f };
        var rounding = Scaled(8f);

        using var button = ImRaii.PushColor(ImGuiCol.Button, fill);
        using var hovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, hoverFill);
        using var active = ImRaii.PushColor(ImGuiCol.ButtonActive, activeFill);
        using var border = ImRaii.PushColor(ImGuiCol.Border, Vector4.Zero);
        using var borderSize = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f);
        using var frameRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, rounding);

        var clicked = ImGui.Button($"##{id}", size);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var iconText = icon.ToIconString();
        var spacing = Scaled(8f);
        var iconMetrics = ResolveSquareIconButtonMetrics(iconText);
        var iconSize = iconMetrics.IconSize;

        var contentPaddingX = Scaled(16f);
        var availableLabelWidth = MathF.Max(Scaled(12f), size.X - (contentPaddingX * 2f) - iconSize.X - spacing);
        var renderedLabel = ClipTextToWidth(label, availableLabelWidth);
        var labelSize = ImGui.CalcTextSize(renderedLabel);
        var totalWidth = iconSize.X + spacing + labelSize.X;
        var contentStartX = min.X + ((size.X - totalWidth) * 0.5f);
        var iconY = min.Y + ((size.Y - iconSize.Y) * 0.5f);
        var labelY = min.Y + ((size.Y - labelSize.Y) * 0.5f);

        drawList.AddRect(min, max, ImGui.GetColorU32(accent with { W = 0.98f }), rounding, ImDrawFlags.None, Scaled(1.3f));
        var accentPillInsetX = Scaled(7f);
        var accentPillInsetY = Scaled(6f);
        var accentPillMin = new Vector2(min.X + accentPillInsetX, min.Y + accentPillInsetY);
        var accentPillMax = new Vector2(accentPillMin.X + Scaled(4f), max.Y - accentPillInsetY);
        drawList.AddRectFilled(
            accentPillMin,
            accentPillMax,
            ImGui.GetColorU32(accent with { W = 0.72f }),
            999f);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            drawList.AddText(
                new Vector2(contentStartX, iconY),
                ImGui.GetColorU32(accent),
                iconText);
        }

        drawList.AddText(
            new Vector2(contentStartX + iconSize.X + spacing, labelY),
            ImGui.GetColorU32(EditorColors.Text),
            renderedLabel);

        return clicked;
    }

    private static bool DrawAccentToggleIconButton(string id, FontAwesomeIcon icon, Vector2 size, bool selected, string? tooltip = null)
    {
        var iconText = icon.ToIconString();
        var accent = selected ? EditorColors.AccentPurple : EditorColors.AccentOrange;
        var fill = selected
            ? accent with { W = 0.18f }
            : EditorColors.ButtonDefault with { W = 0.88f };
        var hoverFill = selected
            ? accent with { W = 0.24f }
            : accent with { W = 0.16f };
        var activeFill = selected
            ? accent with { W = 0.20f }
            : accent with { W = 0.10f };
        var rounding = Scaled(6f);

        using var button = ImRaii.PushColor(ImGuiCol.Button, fill);
        using var hovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, hoverFill);
        using var active = ImRaii.PushColor(ImGuiCol.ButtonActive, activeFill);
        using var border = ImRaii.PushColor(ImGuiCol.Border, Vector4.Zero);
        using var borderSize = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f);
        using var frameRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, rounding);
        var clicked = ImGui.Button($"##{id}", size);

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRect(min, max, ImGui.GetColorU32(accent), rounding, ImDrawFlags.None, Scaled(1f));
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var iconPosition = ResolveCenteredSquareIconPosition(iconText, min, size);
            drawList.AddText(iconPosition, ImGui.GetColorU32(accent), iconText);
        }

        if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(tooltip))
        {
            UiSharedService.AttachToolTip(tooltip);
        }

        return clicked;
    }

    private string ResolveCreatePlacementFolderPath(IReadOnlyList<string> placedFolders)
    {
        var sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(_createPlacementFolderPath);
        if (string.IsNullOrEmpty(sanitizedFolderPath))
        {
            _createPlacementFolderPath = string.Empty;
            return string.Empty;
        }

        if (placedFolders.Any(folder => string.Equals(folder, sanitizedFolderPath, StringComparison.OrdinalIgnoreCase)))
        {
            _createPlacementFolderPath = sanitizedFolderPath;
            return sanitizedFolderPath;
        }

        _createPlacementFolderPath = string.Empty;
        return string.Empty;
    }

    private void SetCreatePlacementFolderPath(string folderPath)
    {
        _createPlacementFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
    }

    private static string BuildCreatePlacementLabel(string label, string folderPath)
    {
        return $"{label} ({ResolveFolderDisplayLabel(folderPath)})";
    }

    private static string BuildCreatePlacementFolderTooltip(string folderPath, bool hasFolders)
    {
        if (!string.IsNullOrEmpty(folderPath))
        {
            return $"Current folder: {ResolveFolderDisplayLabel(folderPath)}\nClick to change or clear it.";
        }

        return hasFolders
            ? "Current folder: Ungrouped\nClick to choose a folder."
            : "Current folder: Ungrouped\nCreate a folder first to assign one.";
    }
}

