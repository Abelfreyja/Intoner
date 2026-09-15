using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Models;
using Intoner.Objects.UI.Components;
using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorToolbar
{
    private const FontAwesomeIcon BoundsToolbarIcon = FontAwesomeIcon.Bullseye;
    private const string BoundsOptionsPopupId = "##sceneBoundsOptionsPopup";
    private static readonly (SceneBoundsCategory Category, string Label, FontAwesomeIcon Icon)[] BoundsFilterOptions =
    [
        (SceneBoundsCategory.Furniture, "Furniture", SceneItemPresentation.ResolveObjectKindIcon(ObjectKind.Furniture)),
        (SceneBoundsCategory.BgObject, "Bg Object", SceneItemPresentation.ResolveObjectKindIcon(ObjectKind.BgObject)),
        (SceneBoundsCategory.Vfx, "VFX", SceneItemPresentation.ResolveObjectKindIcon(ObjectKind.Vfx)),
        (SceneBoundsCategory.Light, "Light", SceneItemPresentation.ResolveObjectKindIcon(ObjectKind.Light)),
        (SceneBoundsCategory.Display, "Display", FontAwesomeIcon.Desktop),
    ];

    private void DrawBoundsToolbarButton(ToolbarSurfaceMode mode)
    {
        SceneBoundsInteractionSettings settings = _gizmo.Settings.BoundsInteractionSettings;
        Vector4? accent = null;
        if (settings.BoundsEnabled)
        {
            accent = EditorColors.BoundsOverlayAccent;
        }
        else if (settings.SelectionEnabled)
        {
            accent = ThemeColors.AccentBlue;
        }

        DrawHeaderActionButton(
            "##sceneBoundsOverlay",
            BoundsToolbarIcon,
            "Target",
            string.Empty,
            IsBoundsOptionsPopupOpen(),
            ToggleBoundsOverlayEnabled,
            accent,
            useAccentFill: false,
            useNeutralHoverFill: true,
            hoverBorderColor: EditorColors.BoundsOverlayAccent,
            drawBackground: DrawBoundsToolbarButtonBackground,
            drawTooltip: DrawBoundsToolbarTooltip,
            mode: mode);

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            ImGui.OpenPopup(BoundsOptionsPopupId);
        }

        DrawBoundsOptionsPopup();
    }

    private void DrawBoundsToolbarButtonBackground(ImDrawListPtr drawList, Vector2 min, Vector2 max, bool hovered, bool active)
    {
        SceneBoundsInteractionSettings settings = _gizmo.Settings.BoundsInteractionSettings;
        var scale = ImGuiHelpers.GlobalScale;
        var inset = 3f * scale;
        var innerMin = min + new Vector2(inset, inset);
        var innerMax = max - new Vector2(inset, inset);
        if (innerMax.X <= innerMin.X || innerMax.Y <= innerMin.Y)
        {
            return;
        }

        var bandGap = MathF.Max(1f * scale, 1f);
        var availableWidth = innerMax.X - innerMin.X;
        var bandWidth = (availableWidth - bandGap) * 0.5f;
        if (bandWidth <= 0f)
        {
            return;
        }

        float alpha = active ? 0.46f : 0.32f;
        if (hovered && !active)
        {
            alpha = 0.40f;
        }

        DrawTransformSnapToolbarBand(
            drawList,
            new Vector2(innerMin.X, innerMin.Y),
            new Vector2(innerMin.X + bandWidth, innerMax.Y),
            ThemeColors.AccentBlue,
            settings.SelectionEnabled,
            alpha);

        var rightMinX = innerMin.X + bandWidth + bandGap;
        DrawTransformSnapToolbarBand(
            drawList,
            new Vector2(rightMinX, innerMin.Y),
            new Vector2(innerMax.X, innerMax.Y),
            EditorColors.BoundsOverlayAccent,
            settings.BoundsEnabled,
            alpha);
    }

    private void DrawBoundsToolbarTooltip()
    {
        SceneBoundsInteractionSettings settings = _gizmo.Settings.BoundsInteractionSettings;
        EditorToolbarTooltip.Draw(
            BoundsToolbarIcon,
            "Target and Bounds",
            EditorColors.BoundsOverlayAccent,
            [
                new("Left click", "Toggle bounds"),
                new("Right click", "Open options"),
            ],
            [
                new(FontAwesomeIcon.MousePointer, "Selection", settings.SelectionEnabled),
                new(FontAwesomeIcon.BorderNone, "Box selection", settings.SelectionEnabled && settings.BoxSelectionEnabled),
                new(FontAwesomeIcon.BorderAll, "Bounds", settings.BoundsEnabled),
                new(FontAwesomeIcon.SlidersH, "Filter", null, BuildBoundsFilterSummary(settings.BoundsFilter)),
                new(FontAwesomeIcon.Bullseye, "Selected only", settings.ShowSelectedOnly),
            ]);
    }

    private void DrawBoundsOptionsPopup()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var accent = EditorColors.BoundsOverlayAccent;
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        Vector2 margin = new(8f * scale);
        Vector2 availableSize = Vector2.Max(Vector2.One, viewport.WorkSize - margin * 2f);
        float popupWidth = MathF.Min(320f * scale, availableSize.X);
        ImGui.SetNextWindowSize(new Vector2(popupWidth, 0f), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new Vector2(popupWidth, 0f), new Vector2(popupWidth, availableSize.Y));

        using var windowPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(10f * scale, 10f * scale));
        using var windowRounding = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, 12f * scale);
        using var popupBg = ImRaii.PushColor(ImGuiCol.PopupBg, ThemeColors.WithAlpha(_editorOverlayLayer.BackgroundColor, 0.98f));
        using var popupBorder = ImRaii.PushColor(ImGuiCol.Border, ThemeColors.WithAlpha(accent, 0.42f));

        using var popup = ImRaii.Popup(BoundsOptionsPopupId);
        if (!popup)
        {
            return;
        }

        ImGui.TextColored(accent, "Target and Bounds");
        ImGuiHelpers.ScaledDummy(4f);

        using (var settingsTable = EditorPropertyTable.Begin("##boundsOptionsSettings"))
        {
            if (settingsTable)
            {
                SceneBoundsInteractionSettings settings = _gizmo.Settings.BoundsInteractionSettings;
                bool selectionEnabled = DrawBoundsOption("boundsSelectionEnabled", "Selection Enabled", settings.SelectionEnabled,
                    "Select scene items with a click. Hold Ctrl to add or remove an item.");
                _gizmo.Settings.BoundsInteractionSettings = settings with
                {
                    SelectionEnabled = selectionEnabled,
                    BoxSelectionEnabled = DrawBoundsOption("boundsBoxSelectionEnabled", "Box Selection", settings.BoxSelectionEnabled,
                        selectionEnabled ? "Drag empty space to select multiple items. Hold Ctrl to add or remove them."
                            : "Enable selection to use box selection.", selectionEnabled),
                    BoundsEnabled = DrawBoundsOption("boundsOverlayEnabled", "Bounds Enabled", settings.BoundsEnabled,
                        "Show bounds for the item types enabled below. Does not change which items can be selected."),
                    ShowSelectedOnly = DrawBoundsOption("boundsSelectedOnly", "Show Selected Only", settings.ShowSelectedOnly,
                        "Limit bounds to selected items. The type filter still applies."),
                };
            }
        }

        ImGuiHelpers.ScaledDummy(6f);
        DrawBoundsFilter();
    }

    private static bool DrawBoundsOption(string id, string label, bool value, string tooltip, bool enabled = true)
    {
        using (ImRaii.Disabled(!enabled))
        {
            EditorPropertyTable.Checkbox(id, label, ref value);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            IntonerTooltip.DrawText(tooltip);
        }

        return value;
    }

    private void DrawBoundsFilter()
    {
        SceneBoundsInteractionSettings settings = _gizmo.Settings.BoundsInteractionSettings;
        float scale = ImGuiHelpers.GlobalScale;
        float right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        int enabledCount = BitOperations.PopCount((uint)(settings.BoundsFilter & SceneBoundsCategory.All));
        string count = $"{enabledCount} / {BoundsFilterOptions.Length}";
        ImGui.TextDisabled("Bounds Filter");
        if (ImGui.IsItemHovered())
        {
            IntonerTooltip.DrawText("Choose which item types show bounds. This does not filter scene selection.");
        }

        ImGui.SameLine();
        ImGui.SetCursorPosX(right - ImGui.GetWindowPos().X - ImGui.CalcTextSize(count).X);
        ImGui.TextDisabled(count);

        float minimumCellWidth = ImGui.GetFrameHeight() + ImGui.CalcTextSize("Furniture").X
            + ImGui.GetStyle().ItemInnerSpacing.X + 36f * scale;
        int columns = ImGui.GetContentRegionAvail().X >= minimumCellWidth * 2f ? 2 : 1;
        using var cellPadding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(4f, 3f) * scale);
        using var table = ImRaii.Table("##boundsFilter", columns,
            ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoPadOuterX | ImGuiTableFlags.NoSavedSettings);
        if (!table)
        {
            return;
        }

        foreach ((SceneBoundsCategory category, string label, FontAwesomeIcon icon) in BoundsFilterOptions)
        {
            ImGui.TableNextColumn();
            float cellRight = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
            bool enabled = settings.Includes(category);
            if (ImGui.Checkbox($"{label}##boundsFilter{category}", ref enabled))
            {
                settings = settings with
                {
                    BoundsFilter = enabled
                        ? settings.BoundsFilter | category
                        : settings.BoundsFilter & ~category,
                };
            }

            Vector2 min = ImGui.GetItemRectMin();
            EditorIcon.DrawCentered(ImGui.GetWindowDrawList(), icon,
                new Vector2(cellRight - 20f * scale, min.Y), new Vector2(cellRight, min.Y + ImGui.GetFrameHeight()),
                enabled ? ThemeColors.Text : ThemeColors.TextDisabled, 0.85f);
        }

        _gizmo.Settings.BoundsInteractionSettings = settings;
    }

    private static string BuildBoundsFilterSummary(SceneBoundsCategory filter)
    {
        if ((filter & SceneBoundsCategory.All) == SceneBoundsCategory.All)
        {
            return "All";
        }

        List<string> enabledKinds = [];
        foreach ((SceneBoundsCategory category, string label, _) in BoundsFilterOptions)
        {
            if ((filter & category) != SceneBoundsCategory.None)
            {
                enabledKinds.Add(label);
            }
        }

        return enabledKinds.Count == 0 ? "None" : string.Join(", ", enabledKinds);
    }

    private static bool IsBoundsOptionsPopupOpen()
        => ImGui.IsPopupOpen(BoundsOptionsPopupId);

    private void ToggleBoundsOverlayEnabled()
        => _gizmo.Settings.BoundsInteractionSettings = _gizmo.Settings.BoundsInteractionSettings with
        {
            BoundsEnabled = !_gizmo.Settings.BoundsInteractionSettings.BoundsEnabled,
        };
}
