using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Models;
using Intoner.Objects.UI.Components;
using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorToolbar
{
    private const string TransformSnapPopupId = "##objectTransformSnapPopup";
    private const float TransformSnapPopupSectionSpacing = 4f;
    private const float TransformSnapStepEqualityTolerance = 0.0001f;
    private static readonly SceneTransformSnapSettings DefaultTransformSnapSettings = GizmoSettings.DefaultTransformSnapSettings;

    private void DrawTransformSnapToolbarButton(ToolbarSurfaceMode mode)
    {
        var accent = ThemeColors.AccentPrimary;
        DrawHeaderActionButton(
            "##objectGizmoSnap",
            FontAwesomeIcon.BorderAll,
            "Snap",
            string.Empty,
            IsTransformSnapPopupOpen(),
            ToggleAllTransformSnapModes,
            AreAnyTransformSnapModesEnabled() ? accent : null,
            useAccentFill: false,
            useNeutralHoverFill: true,
            hoverBorderColor: accent,
            drawBackground: DrawTransformSnapToolbarButtonBackground,
            drawTooltip: DrawTransformSnapToolbarTooltip,
            mode: mode);

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            ImGui.OpenPopup(TransformSnapPopupId);
        }

        var anchorMin = ImGui.GetItemRectMin();
        var anchorMax = ImGui.GetItemRectMax();
        DrawTransformSnapPopup(anchorMin, anchorMax);
    }

    private void DrawTransformSnapToolbarButtonBackground(ImDrawListPtr drawList, Vector2 min, Vector2 max, bool hovered, bool active)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var innerInset = 3f * scale;
        var bandGap = MathF.Max(1f * scale, 1f);
        var innerMin = min + new Vector2(innerInset, innerInset);
        var innerMax = max - new Vector2(innerInset, innerInset);
        var availableWidth = innerMax.X - innerMin.X;
        if (availableWidth <= 0f)
        {
            return;
        }

        var bandWidth = (availableWidth - (bandGap * 2f)) / 3f;
        if (bandWidth <= 0f)
        {
            return;
        }

        float peakAlpha = 0.31f;
        if (active)
        {
            peakAlpha = 0.44f;
        }
        else if (hovered)
        {
            peakAlpha = 0.38f;
        }

        DrawTransformSnapToolbarBand(
            drawList,
            new Vector2(innerMin.X, innerMin.Y),
            new Vector2(innerMin.X + bandWidth, innerMax.Y),
            GetGizmoModeAccentColor(GizmoTransformMode.Translation),
            _gizmo.Settings.TransformSnapSettings.PositionEnabled,
            peakAlpha);

        var centerMinX = innerMin.X + bandWidth + bandGap;
        DrawTransformSnapToolbarBand(
            drawList,
            new Vector2(centerMinX, innerMin.Y),
            new Vector2(centerMinX + bandWidth, innerMax.Y),
            GetGizmoModeAccentColor(GizmoTransformMode.Rotation),
            _gizmo.Settings.TransformSnapSettings.RotationEnabled,
            peakAlpha);

        var rightMinX = centerMinX + bandWidth + bandGap;
        DrawTransformSnapToolbarBand(
            drawList,
            new Vector2(rightMinX, innerMin.Y),
            new Vector2(innerMax.X, innerMax.Y),
            GetGizmoModeAccentColor(GizmoTransformMode.Scale),
            _gizmo.Settings.TransformSnapSettings.ScaleEnabled,
            peakAlpha);
    }

    private static void DrawTransformSnapToolbarBand(ImDrawListPtr drawList, Vector2 min, Vector2 max, Vector4 accent, bool enabled, float peakAlpha)
    {
        if (!enabled || !EditorInputUtility.HasArea(min, max))
        {
            return;
        }

        var midY = min.Y + ((max.Y - min.Y) * 0.56f);
        var middleAlpha = ThemeColors.WithAlpha(accent, peakAlpha);
        var topAlpha = ThemeColors.WithAlpha(accent, peakAlpha * 0.22f);
        var fadeAlpha = ThemeColors.WithAlpha(accent, peakAlpha * 0.18f);

        drawList.AddRectFilledMultiColor(
            min,
            new Vector2(max.X, midY),
            ImGui.GetColorU32(topAlpha),
            ImGui.GetColorU32(topAlpha),
            ImGui.GetColorU32(middleAlpha),
            ImGui.GetColorU32(middleAlpha));

        drawList.AddRectFilledMultiColor(
            new Vector2(min.X, midY),
            max,
            ImGui.GetColorU32(middleAlpha),
            ImGui.GetColorU32(middleAlpha),
            ImGui.GetColorU32(fadeAlpha),
            ImGui.GetColorU32(fadeAlpha));
    }

    private void DrawTransformSnapToolbarTooltip()
        => EditorToolbarTooltip.Draw(
            FontAwesomeIcon.BorderAll,
            "Transform Snap",
            ThemeColors.AccentPrimary,
            [
                new("Left click", "Toggle all modes"),
                new("Right click", "Open options"),
            ],
            [
                new(
                    FontAwesomeIcon.ArrowsAlt,
                    "Position",
                    _gizmo.Settings.TransformSnapSettings.PositionEnabled,
                    FormatTransformSnapStep(_gizmo.Settings.TransformSnapSettings.PositionStep),
                    EditorColors.TransformModeAccent(GizmoTransformMode.Translation)),
                new(
                    FontAwesomeIcon.SyncAlt,
                    "Rotation",
                    _gizmo.Settings.TransformSnapSettings.RotationEnabled,
                    $"{FormatTransformSnapStep(_gizmo.Settings.TransformSnapSettings.RotationStepDegrees)}°",
                    EditorColors.TransformModeAccent(GizmoTransformMode.Rotation)),
                new(
                    FontAwesomeIcon.CompressArrowsAlt,
                    "Scale",
                    _gizmo.Settings.TransformSnapSettings.ScaleEnabled,
                    FormatTransformSnapStep(_gizmo.Settings.TransformSnapSettings.ScaleStep),
                    EditorColors.TransformModeAccent(GizmoTransformMode.Scale)),
                new(
                    FontAwesomeIcon.MousePointer,
                    "Surface dragging",
                    _gizmo.Settings.TransformSnapSettings.PositionDragEnabled,
                    Accent: EditorColors.TransformModeAccent(GizmoTransformMode.Translation)),
            ],
            new EditorToolbarTooltipKeyHint(GizmoInputUtility.PrecisionSnapModifier, "to invert snapping while dragging"));

    private void DrawTransformSnapPopup(Vector2 anchorMin, Vector2 anchorMax)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var accent = ThemeColors.AccentPrimary;
        var defaults = DefaultTransformSnapSettings;
        var popupWidth = 300f * scale;
        ImGui.SetNextWindowPos(new Vector2(anchorMin.X, anchorMax.Y + (8f * scale)), ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(popupWidth, 0f), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new Vector2(popupWidth, 0f), new Vector2(popupWidth, float.MaxValue));

        using var windowPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(9f * scale, 9f * scale));
        using var windowRounding = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, 12f * scale);
        using var popupBg = ImRaii.PushColor(ImGuiCol.PopupBg, ThemeColors.WithAlpha(_editorOverlayLayer.BackgroundColor, 0.98f));
        using var popupBorder = ImRaii.PushColor(ImGuiCol.Border, ThemeColors.WithAlpha(accent, 0.42f));

        using var popup = ImRaii.Popup(TransformSnapPopupId, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!popup)
        {
            return;
        }

        DrawTransformSnapActionRow();
        DrawTransformSnapSectionCards(defaults);

        ImGuiHelpers.ScaledDummy(TransformSnapPopupSectionSpacing);
        DrawTransformSnapFooter();
    }

    private void DrawTransformSnapSectionCards(in SceneTransformSnapSettings defaults)
    {
        ImGuiHelpers.ScaledDummy(TransformSnapPopupSectionSpacing);
        DrawTransformSnapSectionCard(
            "transformSnapPosition",
            FontAwesomeIcon.ArrowsAlt,
            "Position",
            GetGizmoModeAccentColor(GizmoTransformMode.Translation),
            _gizmo.Settings.TransformSnapSettings.PositionEnabled,
            _gizmo.Settings.TransformSnapSettings.PositionStep,
            defaults.PositionStep,
            "%.3f",
            0.001f,
            1000f,
            0.005f,
            step => _gizmo.Settings.TransformSnapSettings = _gizmo.Settings.TransformSnapSettings with { PositionStep = step });

        ImGuiHelpers.ScaledDummy(TransformSnapPopupSectionSpacing);
        DrawTransformSnapSectionCard(
            "transformSnapRotation",
            FontAwesomeIcon.SyncAlt,
            "Rotation",
            GetGizmoModeAccentColor(GizmoTransformMode.Rotation),
            _gizmo.Settings.TransformSnapSettings.RotationEnabled,
            _gizmo.Settings.TransformSnapSettings.RotationStepDegrees,
            defaults.RotationStepDegrees,
            "%.2f",
            0.01f,
            360f,
            0.10f,
            step => _gizmo.Settings.TransformSnapSettings = _gizmo.Settings.TransformSnapSettings with { RotationStepDegrees = step });

        ImGuiHelpers.ScaledDummy(TransformSnapPopupSectionSpacing);
        DrawTransformSnapSectionCard(
            "transformSnapScale",
            FontAwesomeIcon.CompressArrowsAlt,
            "Scale",
            GetGizmoModeAccentColor(GizmoTransformMode.Scale),
            _gizmo.Settings.TransformSnapSettings.ScaleEnabled,
            _gizmo.Settings.TransformSnapSettings.ScaleStep,
            defaults.ScaleStep,
            "%.3f",
            0.001f,
            10f,
            0.005f,
            step => _gizmo.Settings.TransformSnapSettings = _gizmo.Settings.TransformSnapSettings with { ScaleStep = step });
    }

    private void DrawTransformSnapActionRow()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var spacing = 6f * scale;

        DrawTransformSnapModeToggleButton("transformSnapPositionToggle", FontAwesomeIcon.ArrowsAlt, GizmoTransformMode.Translation, _gizmo.Settings.TransformSnapSettings.PositionEnabled, "Toggle position snap");

        ImGui.SameLine(0f, spacing);
        DrawTransformSnapModeToggleButton("transformSnapRotationToggle", FontAwesomeIcon.SyncAlt, GizmoTransformMode.Rotation, _gizmo.Settings.TransformSnapSettings.RotationEnabled, "Toggle rotation snap");

        ImGui.SameLine(0f, spacing);
        DrawTransformSnapModeToggleButton("transformSnapScaleToggle", FontAwesomeIcon.CompressArrowsAlt, GizmoTransformMode.Scale, _gizmo.Settings.TransformSnapSettings.ScaleEnabled, "Toggle scale snap");

        ImGui.SameLine(0f, spacing);
        DrawTransformSnapDragToggleButton();

        ImGui.SameLine(0f, spacing);
        if (DrawTransformSnapActionButton("transformSnapReset", FontAwesomeIcon.Undo, ThemeColors.DimRed, false, "Reset snap settings to defaults", useAccentWhenInactive: true))
        {
            _gizmo.Settings.TransformSnapSettings = DefaultTransformSnapSettings;
        }
    }

    private void DrawTransformSnapDragToggleButton()
    {
        if (!DrawTransformSnapActionButton(
                "transformSnapDraggingToggle",
                FontAwesomeIcon.LocationArrow,
                GetGizmoModeAccentColor(GizmoTransformMode.Translation),
                _gizmo.Settings.TransformSnapSettings.PositionDragEnabled,
                "Toggle snapping for object dragging (based on position snap value)"))
        {
            return;
        }

        _gizmo.Settings.TransformSnapSettings = _gizmo.Settings.TransformSnapSettings with
        {
            PositionDragEnabled = !_gizmo.Settings.TransformSnapSettings.PositionDragEnabled,
        };
    }

    private void DrawTransformSnapModeToggleButton(string id, FontAwesomeIcon icon, GizmoTransformMode mode, bool enabled, string tooltip)
    {
        if (!DrawTransformSnapActionButton(id, icon, GetGizmoModeAccentColor(mode), enabled, tooltip))
        {
            return;
        }

        _gizmo.Settings.TransformSnapSettings = mode switch
        {
            GizmoTransformMode.Translation => _gizmo.Settings.TransformSnapSettings with { PositionEnabled = !_gizmo.Settings.TransformSnapSettings.PositionEnabled },
            GizmoTransformMode.Rotation => _gizmo.Settings.TransformSnapSettings with { RotationEnabled = !_gizmo.Settings.TransformSnapSettings.RotationEnabled },
            GizmoTransformMode.Scale => _gizmo.Settings.TransformSnapSettings with { ScaleEnabled = !_gizmo.Settings.TransformSnapSettings.ScaleEnabled },
            _ => _gizmo.Settings.TransformSnapSettings,
        };
    }

    private static void DrawTransformSnapSectionCard(
        string id,
        FontAwesomeIcon icon,
        string title,
        Vector4 accent,
        bool enabled,
        float step,
        float defaultStep,
        string format,
        float min,
        float max,
        float speed,
        Action<float> apply)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var nextStep = step;
        var changed = false;
        EditorIcon.Metrics iconMetrics = EditorIcon.Measure(icon);
        var iconColumnWidth = MathF.Max(24f * scale, iconMetrics.Size.X + (8f * scale));

        EditorCard.DrawPanelCard(
            id,
            ThemeColors.WithAlpha(ThemeColors.ButtonDefault, enabled ? 0.28f : 0.18f),
            ThemeColors.WithAlpha(accent, enabled ? 0.42f : 0.20f),
            8f * scale,
            new Vector2(8f * scale, 6f * scale),
            () =>
            {
                using var table = ImRaii.Table($"##{id}Row", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoPadInnerX | ImGuiTableFlags.NoPadOuterX);
                if (table)
                {
                    ImGui.TableSetupColumn("Icon", ImGuiTableColumnFlags.WidthFixed, iconColumnWidth);
                    ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, 58f * scale);
                    ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 1f);
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImDrawListPtr drawList = ImGui.GetWindowDrawList();
                    Vector2 iconAreaMin = ImGui.GetCursorScreenPos();
                    Vector2 iconAreaSize = new(iconColumnWidth - (2f * scale), ImGui.GetFrameHeight());
                    ImGui.Dummy(iconAreaSize);
                    EditorIcon.DrawCentered(
                        drawList,
                        icon,
                        iconAreaMin,
                        iconAreaMin + iconAreaSize,
                        accent);

                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted(title);

                    ImGui.TableNextColumn();
                    var buttonWidth = 24f * scale;
                    var controlGap = 4f * scale;
                    var inputWidth = MathF.Max(40f * scale, ImGui.GetContentRegionAvail().X - (buttonWidth * 2f) - (controlGap * 2f));

                    if (DrawTransformSnapStepButton($"##{id}StepDown", "-", accent, new Vector2(buttonWidth, 0f)))
                    {
                        changed = UpdateTransformSnapStep(ref nextStep, nextStep - speed, step, min, max, step);
                    }

                    ImGui.SameLine(0f, controlGap);
                    ImGui.SetNextItemWidth(inputWidth);
                    using var border = ImRaii.PushColor(ImGuiCol.Border, ThemeColors.WithAlpha(accent, 0.90f));
                    using var borderSize = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 1f * scale);
                    if (ImGui.DragFloat($"##{id}Step", ref nextStep, speed, min, max, format))
                    {
                        changed = UpdateTransformSnapStep(ref nextStep, nextStep, step, min, max, step);
                    }
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && !AreTransformSnapStepsEqual(step, defaultStep))
                    {
                        changed = UpdateTransformSnapStep(ref nextStep, defaultStep, step, min, max, defaultStep);
                    }

                    ImGui.SameLine(0f, controlGap);
                    if (DrawTransformSnapStepButton($"##{id}StepUp", "+", accent, new Vector2(buttonWidth, 0f)))
                    {
                        changed = UpdateTransformSnapStep(ref nextStep, nextStep + speed, step, min, max, step);
                    }
                }
            });

        if (changed)
        {
            apply(nextStep);
        }
    }

    private static void DrawTransformSnapFooter()
    {
        IntonerTooltipContent.KeyHint("Off: hold", GizmoInputUtility.PrecisionSnapModifier, "to apply snap.");
        IntonerTooltipContent.KeyHint("On: hold", GizmoInputUtility.PrecisionSnapModifier, "to bypass snap.");
    }

    private static bool DrawTransformSnapActionButton(string id, FontAwesomeIcon icon, Vector4 accent, bool selected, string tooltip, bool useAccentWhenInactive = false)
    {
        var scale = ImGuiHelpers.GlobalScale;
        EditorIcon.Metrics iconMetrics = EditorIcon.Measure(icon);
        var edge = MathF.Max(30f * scale, EditorIconButton.MeasureEdge(icon));
        var size = new Vector2(edge, edge);
        var inactiveAccent = ThemeColors.AccentGrey;
        var currentAccent = selected || useAccentWhenInactive
            ? accent
            : inactiveAccent;
        var fill = selected
            ? ThemeColors.WithAlpha(currentAccent, 0.20f)
            : ThemeColors.WithAlpha(ThemeColors.ButtonDefault, 0.80f);
        float inactiveHoverAlpha = useAccentWhenInactive ? 0.12f : 0.08f;
        float inactiveActiveAlpha = useAccentWhenInactive ? 0.18f : 0.12f;
        var hoverFill = ThemeColors.WithAlpha(currentAccent, selected ? 0.30f : inactiveHoverAlpha);
        var activeFill = ThemeColors.WithAlpha(currentAccent, selected ? 0.36f : inactiveActiveAlpha);
        var rounding = 7f * scale;

        using var button = ImRaii.PushColor(ImGuiCol.Button, fill);
        using var hovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, hoverFill);
        using var active = ImRaii.PushColor(ImGuiCol.ButtonActive, activeFill);
        using var border = ImRaii.PushColor(ImGuiCol.Border, Vector4.Zero);
        using var borderSize = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, MathF.Max(1f * scale, 1f));
        using var frameRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, rounding);

        var clicked = ImGui.Button($"##{id}", size);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();

        float borderAlpha;
        if (selected)
        {
            borderAlpha = 0.88f;
        }
        else
        {
            borderAlpha = useAccentWhenInactive ? 0.42f : 0.28f;
        }

        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(ThemeColors.WithAlpha(currentAccent, borderAlpha)),
            rounding,
            ImDrawFlags.None,
            MathF.Max(1f * scale, 1f));

        float iconAlpha;
        if (selected)
        {
            iconAlpha = 1f;
        }
        else
        {
            iconAlpha = useAccentWhenInactive ? 0.88f : 0.72f;
        }

        EditorIcon.Draw(
            drawList,
            icon,
            iconMetrics,
            min + ((size - iconMetrics.Size) * 0.5f),
            ThemeColors.WithAlpha(currentAccent, iconAlpha));

        if (ImGui.IsItemHovered())
        {
            IntonerTooltip.DrawDescription(
                icon,
                tooltip,
                options: new IntonerTooltipOptions { Accent = currentAccent });
        }

        return clicked;
    }

    private bool AreAnyTransformSnapModesEnabled()
        => _gizmo.Settings.TransformSnapSettings.PositionEnabled
           || _gizmo.Settings.TransformSnapSettings.RotationEnabled
           || _gizmo.Settings.TransformSnapSettings.ScaleEnabled;

    private bool AreAllTransformSnapModesEnabled()
        => _gizmo.Settings.TransformSnapSettings.PositionEnabled
           && _gizmo.Settings.TransformSnapSettings.RotationEnabled
           && _gizmo.Settings.TransformSnapSettings.ScaleEnabled;

    private void ToggleAllTransformSnapModes()
    {
        var enableAll = !AreAllTransformSnapModesEnabled();
        _gizmo.Settings.TransformSnapSettings = _gizmo.Settings.TransformSnapSettings with
        {
            PositionEnabled = enableAll,
            RotationEnabled = enableAll,
            ScaleEnabled = enableAll,
        };
    }

    private static string FormatTransformSnapStep(float step)
        => step.ToString("0.###");

    private static bool AreTransformSnapStepsEqual(float left, float right)
        => NumericsUtility.IsNearlyEqual(left, right, TransformSnapStepEqualityTolerance);

    private static bool UpdateTransformSnapStep(ref float currentStep, float candidateStep, float originalStep, float min, float max, float fallbackStep)
    {
        currentStep = SanitizeTransformSnapStep(candidateStep, min, max, fallbackStep);
        return !AreTransformSnapStepsEqual(originalStep, currentStep);
    }

    private static float SanitizeTransformSnapStep(float value, float min, float max, float fallback)
    {
        if (!float.IsFinite(value))
        {
            value = fallback;
        }

        return Math.Clamp(value, min, max);
    }

    private static bool DrawTransformSnapStepButton(string id, string label, Vector4 accent, Vector2 size)
    {
        using var button = ImRaii.PushColor(ImGuiCol.Button, ThemeColors.WithAlpha(ThemeColors.ButtonDefault, 0.82f));
        using var hovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, ThemeColors.WithAlpha(accent, 0.14f));
        using var active = ImRaii.PushColor(ImGuiCol.ButtonActive, ThemeColors.WithAlpha(accent, 0.20f));
        using var border = ImRaii.PushColor(ImGuiCol.Border, ThemeColors.WithAlpha(accent, 0.56f));
        using var borderSize = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, MathF.Max(1f * ImGuiHelpers.GlobalScale, 1f));
        using var frameRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 6f * ImGuiHelpers.GlobalScale);
        return ImGui.Button($"{label}{id}", size);
    }

    private static bool IsTransformSnapPopupOpen()
        => ImGui.IsPopupOpen(TransformSnapPopupId);
}
