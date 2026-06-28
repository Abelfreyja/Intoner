using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private const string SurfaceAlignPopupId = "##objectSurfaceAlignPopup";

    private bool SurfaceAlignToNormal
    {
        get => _gizmo.Settings.SurfaceAlignToNormal;
        set => _gizmo.Settings.SurfaceAlignToNormal = value;
    }

    private bool SurfaceObjectTargetsEnabled
    {
        get => _gizmo.Settings.SurfaceObjectTargetsEnabled;
        set => _gizmo.Settings.SurfaceObjectTargetsEnabled = value;
    }

    private SurfaceObjectTargetShape SurfaceObjectTargetShape
    {
        get => _gizmo.Settings.SurfaceObjectTargetShape;
        set => _gizmo.Settings.SurfaceObjectTargetShape = value;
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
            hoverBorderColor: EditorColors.AccentGreen,
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

        var alpha = active
            ? 0.44f
            : hovered
                ? 0.38f
                : 0.31f;

        DrawTransformSnapToolbarBand(
            drawList,
            new Vector2(innerMin.X, innerMin.Y),
            new Vector2(innerMin.X + bandWidth, innerMax.Y),
            EditorColors.AccentGreen,
            SurfaceAlignToNormal,
            alpha);

        var rightMinX = innerMin.X + bandWidth + bandGap;
        DrawTransformSnapToolbarBand(
            drawList,
            new Vector2(rightMinX, innerMin.Y),
            new Vector2(innerMax.X, innerMax.Y),
            EditorColors.AccentYellow,
            SurfaceObjectTargetsEnabled,
            alpha);
    }

    private void DrawSurfaceAlignToolbarTooltip()
        => DrawToolbarTooltip(EditorColors.AccentGreen, () =>
        {
            ImGui.TextColored(EditorColors.AccentGreen, "Surface Align");
            ImGui.TextDisabled("Left click to toggle normal alignment.");
            ImGui.TextDisabled("Right click to open align options.");
            ImGui.Spacing();
            ImGui.TextUnformatted($"Normal: {FormatOnOff(SurfaceAlignToNormal)}");
            ImGui.TextUnformatted($"Object Targets: {FormatOnOff(SurfaceObjectTargetsEnabled)}");
            ImGui.TextUnformatted($"Target Shape: {FormatSurfaceObjectTargetShape(SurfaceObjectTargetShape)}");
        });

    private void DrawSurfaceAlignPopup(Vector2 anchorMin, Vector2 anchorMax)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var accent = EditorColors.AccentGreen;
        var popupWidth = 320f * scale;
        ImGui.SetNextWindowPos(new Vector2(anchorMin.X, anchorMax.Y + (8f * scale)), ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(popupWidth, 0f), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new Vector2(popupWidth, 0f), new Vector2(popupWidth, float.MaxValue));

        using var windowPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(10f * scale, 10f * scale));
        using var windowRounding = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, 12f * scale);
        using var popupBg = ImRaii.PushColor(ImGuiCol.PopupBg, EditorColors.WithAlpha(_windowBodyBackgroundColor, 0.98f));
        using var popupBorder = ImRaii.PushColor(ImGuiCol.Border, EditorColors.WithAlpha(accent, 0.42f));

        using var popup = ImRaii.Popup(SurfaceAlignPopupId, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!popup)
        {
            return;
        }

        ImGui.TextColored(accent, "Surface Align");
        ImGui.TextDisabled("Settings for gizmo surface dragging.");
        ImGuiHelpers.ScaledDummy(4f);

        using var settingsTable = CompactSettingsTable("##surfaceAlignOptionsSettings");
        if (!settingsTable)
        {
            return;
        }

        var alignToNormal = SurfaceAlignToNormal;
        if (DrawCheckboxRow("surfaceAlignNormal", "Align To Normal", ref alignToNormal))
        {
            SurfaceAlignToNormal = alignToNormal;
        }

        var objectTargets = SurfaceObjectTargetsEnabled;
        if (DrawCheckboxRow("surfaceObjectTargets", "Object Targets", ref objectTargets))
        {
            SurfaceObjectTargetsEnabled = objectTargets;
        }

        var targetShape = SurfaceObjectTargetShape;
        using (ImRaii.Disabled(!SurfaceObjectTargetsEnabled))
        {
            if (DrawSurfaceObjectTargetShapeRow(ref targetShape))
            {
                SurfaceObjectTargetShape = targetShape;
            }
        }
    }

    private Vector4? ResolveSurfaceAlignAccentColor()
    {
        if (SurfaceAlignToNormal)
        {
            return EditorColors.AccentGreen;
        }

        return SurfaceObjectTargetsEnabled
            ? EditorColors.AccentYellow
            : null;
    }

    private static bool DrawSurfaceObjectTargetShapeRow(ref SurfaceObjectTargetShape value)
    {
        DrawCompactSettingsLabelCell("Target Shape");
        bool changed = DrawSurfaceObjectTargetShapeButton(
            "surfaceObjectTargetBounds",
            "Bounds",
            "Uses the object's current bounds as the drag target.",
            SurfaceObjectTargetShape.Bounds,
            ref value);

        ImGui.SameLine();
        changed |= DrawSurfaceObjectTargetShapeButton(
            "surfaceObjectTargetGeometry",
            "Geometry",
            "Uses rendered object geometry when available.",
            SurfaceObjectTargetShape.Geometry,
            ref value);
        return changed;
    }

    private static bool DrawSurfaceObjectTargetShapeButton(
        string id,
        string label,
        string tooltip,
        SurfaceObjectTargetShape option,
        ref SurfaceObjectTargetShape value)
    {
        var selected = value == option;
        using var selectedButton = selected
            ? ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive))
            : default;

        var changed = ImGui.Button($"{label}##{id}") && !selected;
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }

        if (changed)
        {
            value = option;
        }

        return changed;
    }

    private static string FormatSurfaceObjectTargetShape(SurfaceObjectTargetShape shape)
        => shape switch
        {
            SurfaceObjectTargetShape.Bounds => "Bounds",
            SurfaceObjectTargetShape.Geometry => "Geometry",
            _ => "Unknown",
        };

    private static bool IsSurfaceAlignPopupOpen()
        => ImGui.IsPopupOpen(SurfaceAlignPopupId);

    private void ToggleSurfaceAlignToNormal()
        => SurfaceAlignToNormal = !SurfaceAlignToNormal;
}

