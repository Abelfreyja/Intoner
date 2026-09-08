using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.UI.Components;
using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorToolbar
{
    private const FontAwesomeIcon BoundsToolbarIcon = FontAwesomeIcon.Bullseye;
    private const string BoundsOptionsPopupId = "##sceneBoundsOptionsPopup";

    private void DrawBoundsToolbarButton(ToolbarSurfaceMode mode)
    {
        Vector4? accent = null;
        if (_gizmo.Settings.BoundsInteractionSettings.BoundsEnabled)
        {
            accent = EditorColors.BoundsOverlayAccent;
        }
        else if (_gizmo.Settings.BoundsInteractionSettings.SelectionEnabled)
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

        var anchorMin = ImGui.GetItemRectMin();
        var anchorMax = ImGui.GetItemRectMax();
        DrawBoundsOptionsPopup(anchorMin, anchorMax);
    }

    private void DrawBoundsToolbarButtonBackground(ImDrawListPtr drawList, Vector2 min, Vector2 max, bool hovered, bool active)
    {
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
            _gizmo.Settings.BoundsInteractionSettings.SelectionEnabled,
            alpha);

        var rightMinX = innerMin.X + bandWidth + bandGap;
        DrawTransformSnapToolbarBand(
            drawList,
            new Vector2(rightMinX, innerMin.Y),
            new Vector2(innerMax.X, innerMax.Y),
            EditorColors.BoundsOverlayAccent,
            _gizmo.Settings.BoundsInteractionSettings.BoundsEnabled,
            alpha);
    }

    private void DrawBoundsToolbarTooltip()
        => EditorToolbarTooltip.Draw(
            BoundsToolbarIcon,
            "Target and Bounds",
            EditorColors.BoundsOverlayAccent,
            [
                new("Left click", "Toggle bounds"),
                new("Right click", "Open options"),
            ],
            [
                new(FontAwesomeIcon.MousePointer, "Selection", _gizmo.Settings.BoundsInteractionSettings.SelectionEnabled),
                new(FontAwesomeIcon.BorderAll, "Bounds", _gizmo.Settings.BoundsInteractionSettings.BoundsEnabled),
                new(
                    FontAwesomeIcon.SlidersH,
                    "Filter",
                    null,
                    BuildBoundsFilterSummary(_gizmo.Settings.BoundsInteractionSettings.BoundsFilter)),
                new(FontAwesomeIcon.Bullseye, "Selected only", _gizmo.Settings.BoundsInteractionSettings.ShowSelectedOnly),
            ]);

    private void DrawBoundsOptionsPopup(Vector2 anchorMin, Vector2 anchorMax)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var accent = EditorColors.BoundsOverlayAccent;
        var popupWidth = 320f * scale;
        ImGui.SetNextWindowPos(new Vector2(anchorMin.X, anchorMax.Y + (8f * scale)), ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(popupWidth, 0f), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new Vector2(popupWidth, 0f), new Vector2(popupWidth, float.MaxValue));

        using var windowPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(10f * scale, 10f * scale));
        using var windowRounding = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, 12f * scale);
        using var popupBg = ImRaii.PushColor(ImGuiCol.PopupBg, ThemeColors.WithAlpha(_editorOverlayLayer.BackgroundColor, 0.98f));
        using var popupBorder = ImRaii.PushColor(ImGuiCol.Border, ThemeColors.WithAlpha(accent, 0.42f));

        using var popup = ImRaii.Popup(BoundsOptionsPopupId, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!popup)
        {
            return;
        }

        ImGui.TextColored(accent, "Target and Bounds");
        ImGui.TextDisabled("Settings to change selection and bounds behavior.");
        ImGuiHelpers.ScaledDummy(4f);

        using (var settingsTable = EditorPropertyTable.Begin("##boundsOptionsSettings"))
        {
            if (settingsTable)
            {
                var selectionEnabled = _gizmo.Settings.BoundsInteractionSettings.SelectionEnabled;
                if (EditorPropertyTable.Checkbox("boundsSelectionEnabled", "Selection Enabled", ref selectionEnabled))
                {
                    _gizmo.Settings.BoundsInteractionSettings = _gizmo.Settings.BoundsInteractionSettings with { SelectionEnabled = selectionEnabled };
                }

                var boundsEnabled = _gizmo.Settings.BoundsInteractionSettings.BoundsEnabled;
                if (EditorPropertyTable.Checkbox("boundsOverlayEnabled", "Bounds Enabled", ref boundsEnabled))
                {
                    _gizmo.Settings.BoundsInteractionSettings = _gizmo.Settings.BoundsInteractionSettings with { BoundsEnabled = boundsEnabled };
                }

                var showSelectedOnly = _gizmo.Settings.BoundsInteractionSettings.ShowSelectedOnly;
                if (EditorPropertyTable.Checkbox("boundsSelectedOnly", "Show Selected Only", ref showSelectedOnly))
                {
                    _gizmo.Settings.BoundsInteractionSettings = _gizmo.Settings.BoundsInteractionSettings with { ShowSelectedOnly = showSelectedOnly };
                }
            }
        }

        ImGuiHelpers.ScaledDummy(6f);
        ImGui.TextDisabled("Bounds Filter");
        ImGuiHelpers.ScaledDummy(2f);

        DrawBoundsFilterCheckbox("boundsFilterFurniture", "Furniture", SceneBoundsCategory.Furniture);
        DrawBoundsFilterCheckbox("boundsFilterBgObject", "Bg Object", SceneBoundsCategory.BgObject);
        DrawBoundsFilterCheckbox("boundsFilterVfx", "VFX", SceneBoundsCategory.Vfx);
        DrawBoundsFilterCheckbox("boundsFilterLight", "Light", SceneBoundsCategory.Light);
        DrawBoundsFilterCheckbox("boundsFilterDisplay", "Display", SceneBoundsCategory.Display);
    }

    private void DrawBoundsFilterCheckbox(string id, string label, SceneBoundsCategory category)
    {
        var enabled = _gizmo.Settings.BoundsInteractionSettings.Includes(category);
        if (!ImGui.Checkbox($"{label}##{id}", ref enabled))
        {
            return;
        }

        _gizmo.Settings.BoundsInteractionSettings = _gizmo.Settings.BoundsInteractionSettings with
        {
            BoundsFilter = enabled
                ? _gizmo.Settings.BoundsInteractionSettings.BoundsFilter | category
                : _gizmo.Settings.BoundsInteractionSettings.BoundsFilter & ~category,
        };
    }

    private static string BuildBoundsFilterSummary(SceneBoundsCategory filter)
    {
        if ((filter & SceneBoundsCategory.All) == SceneBoundsCategory.All)
        {
            return "All";
        }

        List<string> enabledKinds = [];
        if ((filter & SceneBoundsCategory.Furniture) != SceneBoundsCategory.None)
        {
            enabledKinds.Add("Furniture");
        }

        if ((filter & SceneBoundsCategory.BgObject) != SceneBoundsCategory.None)
        {
            enabledKinds.Add("Bg Object");
        }

        if ((filter & SceneBoundsCategory.Vfx) != SceneBoundsCategory.None)
        {
            enabledKinds.Add("VFX");
        }

        if ((filter & SceneBoundsCategory.Light) != SceneBoundsCategory.None)
        {
            enabledKinds.Add("Light");
        }

        if ((filter & SceneBoundsCategory.Display) != SceneBoundsCategory.None)
        {
            enabledKinds.Add("Display");
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
