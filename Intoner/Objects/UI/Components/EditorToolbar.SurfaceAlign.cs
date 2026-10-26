using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Utility;
using Dalamud.Interface;
using Intoner.Objects.UI.Components;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorToolbar
{
    private const string SurfaceAlignPopupId = "##objectSurfaceAlignPopup";

    private void DrawSurfaceAlignToolbarButton(ToolbarSurfaceMode mode)
    {
        var accent = ResolveSurfaceAlignAccentColor();
        DrawHeaderActionButton(
            "##objectGizmoSurfaceAlign",
            FontAwesomeIcon.Compass,
            "Align",
            string.Empty,
            IsSurfaceAlignPopupOpen(),
            ToggleSurfaceAlignToNormal,
            accent,
            useNeutralHoverFill: true,
            hoverBorderColor: ThemeColors.AccentGreen,
            drawBackground: DrawSurfaceAlignToolbarButtonBackground,
            drawTooltip: DrawSurfaceAlignToolbarTooltip,
            mode: mode);

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            ImGui.OpenPopup(SurfaceAlignPopupId);
        }

        var anchorMin = ImGui.GetItemRectMin();
        var anchorMax = ImGui.GetItemRectMax();
        DrawSurfaceAlignPopup(anchorMin, anchorMax);
    }

    private void DrawSurfaceAlignToolbarButtonBackground(ImDrawListPtr drawList, Vector2 min, Vector2 max, bool hovered, bool active)
    {
        float peakAlpha = hovered ? 0.38f : 0.31f;
        if (active)
        {
            peakAlpha = 0.44f;
        }
        DrawToolbarStateBands(drawList, min, max,
            [(ThemeColors.AccentGreen, _gizmo.Settings.SurfaceAlignToNormal), (ThemeColors.AccentYellow, _gizmo.Settings.SurfaceItemTargetsEnabled)],
            peakAlpha);
    }

    private void DrawSurfaceAlignToolbarTooltip()
        => EditorToolbarTooltip.Draw(
            FontAwesomeIcon.Compass,
            "Surface Align",
            ThemeColors.AccentGreen,
            [
                new("Left click", "Toggle normal alignment"),
                new("Right click", "Open options"),
            ],
            [
                new(
                    FontAwesomeIcon.Compass,
                    "Normal alignment",
                    _gizmo.Settings.SurfaceAlignToNormal,
                    Accent: ThemeColors.AccentGreen),
                new(
                    FontAwesomeIcon.Cubes,
                    "Scene item targets",
                    _gizmo.Settings.SurfaceItemTargetsEnabled,
                    Accent: ThemeColors.AccentYellow),
                new(
                    FontAwesomeIcon.BorderAll,
                    "Target shape",
                    null,
                    FormatSceneSurfaceTargetShape(_gizmo.Settings.SurfaceTargetShape),
                    ThemeColors.AccentYellow),
            ],
            []);

    private void DrawSurfaceAlignPopup(Vector2 anchorMin, Vector2 anchorMax)
    {
        if (!IsSurfaceAlignPopupOpen())
        {
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var accent = ThemeColors.AccentGreen;
        var popupWidth = 320f * scale;
        ImGui.SetNextWindowPos(new Vector2(anchorMin.X, anchorMax.Y + (8f * scale)), ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(popupWidth, 0f), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new Vector2(popupWidth, 0f), new Vector2(popupWidth, float.MaxValue));

        using var windowPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(10f * scale, 10f * scale));
        using var windowRounding = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, 12f * scale);
        using var popupBg = ImRaii.PushColor(ImGuiCol.PopupBg, ThemeColors.WithAlpha(_editorOverlayLayer.BackgroundColor, 0.98f));
        using var popupBorder = ImRaii.PushColor(ImGuiCol.Border, ThemeColors.WithAlpha(accent, 0.42f));

        using var popup = ImRaii.Popup(SurfaceAlignPopupId, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!popup)
        {
            return;
        }

        ImGui.TextColored(accent, "Surface Align");
        ImGui.TextDisabled("Settings for gizmo surface dragging.");
        ImGuiHelpers.ScaledDummy(4f);

        using var settingsTable = EditorPropertyTable.Begin("##surfaceAlignOptionsSettings");
        if (!settingsTable)
        {
            return;
        }

        var alignToNormal = _gizmo.Settings.SurfaceAlignToNormal;
        if (EditorPropertyTable.Checkbox("surfaceAlignNormal", "Align To Normal", ref alignToNormal))
        {
            _gizmo.Settings.SurfaceAlignToNormal = alignToNormal;
        }

        var itemTargets = _gizmo.Settings.SurfaceItemTargetsEnabled;
        if (EditorPropertyTable.Checkbox("surfaceItemTargets", "Scene Item Targets", ref itemTargets))
        {
            _gizmo.Settings.SurfaceItemTargetsEnabled = itemTargets;
        }

        var targetShape = _gizmo.Settings.SurfaceTargetShape;
        if (DrawSceneSurfaceTargetShapeRow(ref targetShape))
        {
            _gizmo.Settings.SurfaceTargetShape = targetShape;
        }
    }

    private Vector4? ResolveSurfaceAlignAccentColor()
    {
        if (_gizmo.Settings.SurfaceAlignToNormal)
        {
            return ThemeColors.AccentGreen;
        }

        return _gizmo.Settings.SurfaceItemTargetsEnabled
            ? ThemeColors.AccentYellow
            : null;
    }

    private static bool DrawSceneSurfaceTargetShapeRow(ref SceneSurfaceTargetShape value)
    {
        EditorPropertyTable.NextRow("Target Shape");
        bool changed = DrawSceneSurfaceTargetShapeButton(
            "surfaceItemTargetBounds",
            "Bounds",
            "Uses each scene item's current bounds as a drag target.",
            SceneSurfaceTargetShape.Bounds,
            ref value);

        ImGui.SameLine();
        changed |= DrawSceneSurfaceTargetShapeButton(
            "surfaceItemTargetGeometry",
            "Geometry",
            "Uses rendered scene item geometry when available.",
            SceneSurfaceTargetShape.Geometry,
            ref value);
        return changed;
    }

    private static bool DrawSceneSurfaceTargetShapeButton(
        string id,
        string label,
        string tooltip,
        SceneSurfaceTargetShape option,
        ref SceneSurfaceTargetShape value)
    {
        var selected = value == option;
        using var selectedButton = selected
            ? ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive))
            : default;

        var changed = ImGui.Button($"{label}##{id}") && !selected;
        if (ImGui.IsItemHovered())
        {
            IntonerTooltip.DrawText(tooltip);
        }

        if (changed)
        {
            value = option;
        }

        return changed;
    }

    private static string FormatSceneSurfaceTargetShape(SceneSurfaceTargetShape shape)
        => shape switch
        {
            SceneSurfaceTargetShape.Bounds => "Bounds",
            SceneSurfaceTargetShape.Geometry => "Geometry",
            _ => "Unknown",
        };

    private static bool IsSurfaceAlignPopupOpen()
        => ImGui.IsPopupOpen(SurfaceAlignPopupId);

    private void ToggleSurfaceAlignToNormal()
        => _gizmo.Settings.SurfaceAlignToNormal = !_gizmo.Settings.SurfaceAlignToNormal;
}
