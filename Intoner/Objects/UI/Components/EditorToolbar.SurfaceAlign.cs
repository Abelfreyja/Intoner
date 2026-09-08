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

    private bool SurfaceAlignToNormal
    {
        get => _gizmo.Settings.SurfaceAlignToNormal;
        set => _gizmo.Settings.SurfaceAlignToNormal = value;
    }

    private bool SurfaceItemTargetsEnabled
    {
        get => _gizmo.Settings.SurfaceItemTargetsEnabled;
        set => _gizmo.Settings.SurfaceItemTargetsEnabled = value;
    }

    private SceneSurfaceTargetShape SurfaceTargetShape
    {
        get => _gizmo.Settings.SurfaceTargetShape;
        set => _gizmo.Settings.SurfaceTargetShape = value;
    }

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
            useAccentFill: false,
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
        var scale = ImGuiHelpers.GlobalScale;
        var inset = 3f * scale;
        var innerMin = min + new Vector2(inset, inset);
        var innerMax = max - new Vector2(inset, inset);
        if (innerMax.X <= innerMin.X || innerMax.Y <= innerMin.Y)
        {
            return;
        }

        var bandGap = MathF.Max(1f * scale, 1f);
        var bandWidth = ((innerMax.X - innerMin.X) - bandGap) * 0.5f;
        if (bandWidth <= 0f)
        {
            return;
        }

        float alpha = 0.31f;
        if (active)
        {
            alpha = 0.44f;
        }
        else if (hovered)
        {
            alpha = 0.38f;
        }

        DrawTransformSnapToolbarBand(
            drawList,
            new Vector2(innerMin.X, innerMin.Y),
            new Vector2(innerMin.X + bandWidth, innerMax.Y),
            ThemeColors.AccentGreen,
            SurfaceAlignToNormal,
            alpha);

        var rightMinX = innerMin.X + bandWidth + bandGap;
        DrawTransformSnapToolbarBand(
            drawList,
            new Vector2(rightMinX, innerMin.Y),
            new Vector2(innerMax.X, innerMax.Y),
            ThemeColors.AccentYellow,
            SurfaceItemTargetsEnabled,
            alpha);
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
                    SurfaceAlignToNormal,
                    Accent: ThemeColors.AccentGreen),
                new(
                    FontAwesomeIcon.Cubes,
                    "Scene item targets",
                    SurfaceItemTargetsEnabled,
                    Accent: ThemeColors.AccentYellow),
                new(
                    FontAwesomeIcon.BorderAll,
                    "Target shape",
                    null,
                    FormatSceneSurfaceTargetShape(SurfaceTargetShape),
                    ThemeColors.AccentYellow),
            ]);

    private void DrawSurfaceAlignPopup(Vector2 anchorMin, Vector2 anchorMax)
    {
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

        var alignToNormal = SurfaceAlignToNormal;
        if (EditorPropertyTable.Checkbox("surfaceAlignNormal", "Align To Normal", ref alignToNormal))
        {
            SurfaceAlignToNormal = alignToNormal;
        }

        var itemTargets = SurfaceItemTargetsEnabled;
        if (EditorPropertyTable.Checkbox("surfaceItemTargets", "Scene Item Targets", ref itemTargets))
        {
            SurfaceItemTargetsEnabled = itemTargets;
        }

        var targetShape = SurfaceTargetShape;
        if (DrawSceneSurfaceTargetShapeRow(ref targetShape))
        {
            SurfaceTargetShape = targetShape;
        }
    }

    private Vector4? ResolveSurfaceAlignAccentColor()
    {
        if (SurfaceAlignToNormal)
        {
            return ThemeColors.AccentGreen;
        }

        return SurfaceItemTargetsEnabled
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
        => SurfaceAlignToNormal = !SurfaceAlignToNormal;
}
