using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private const string TransformSnapPopupId = "##objectTransformSnapPopup";
    private const float TransformSnapPopupSectionSpacing = 4f;
    private const float TransformSnapStepEqualityTolerance = 0.0001f;

    private static readonly ObjectTransformSnapSettings DefaultTransformSnapSettings = GizmoSettings.DefaultTransformSnapSettings;

    private ObjectTransformSnapSettings TransformSnapSettings
    {
        get => _gizmo.Settings.TransformSnapSettings;
        set => _gizmo.Settings.TransformSnapSettings = value;
    }

    private static ObjectSnapBasis WorldTransformSnapBasis
        => GizmoSnapBasisUtility.World;

    private bool IsPositionSnapActive()
        => ResolveTransformSnapActive(TransformSnapSettings.PositionEnabled);

    private bool IsRotationSnapActive()
        => ResolveTransformSnapActive(TransformSnapSettings.RotationEnabled);

    private bool IsScaleSnapActive()
        => ResolveTransformSnapActive(TransformSnapSettings.ScaleEnabled);

    private static bool ResolveTransformSnapActive(bool alwaysEnabled)
        => alwaysEnabled
            ? !GizmoInputUtility.IsPrecisionSnapModifierActive()
            : GizmoInputUtility.IsPrecisionSnapModifierActive();

    private Vector3 ApplyPositionSnap(Vector3 position, in ObjectSnapBasis basis)
        => IsPositionSnapActive()
            ? ObjectTransformSnapUtility.SnapPosition(position, TransformSnapSettings.PositionStep, basis)
            : position;

    private Vector3 ApplyRotationSnap(Vector3 rotationDegrees)
        => IsRotationSnapActive()
            ? ObjectTransformSnapUtility.SnapRotationDegrees(rotationDegrees, TransformSnapSettings.RotationStepDegrees)
            : rotationDegrees;

    private Vector3 ApplyScaleSnap(Vector3 scale)
        => IsScaleSnapActive()
            ? ObjectTransformSnapUtility.SnapScale(scale, TransformSnapSettings.ScaleStep)
            : scale;

    private void DrawTransformSnapToolbarButton(ToolbarSurfaceMode mode)
    {
        var accent = EditorColors.AccentPurple;
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

        var peakAlpha = active
            ? 0.44f
            : hovered
                ? 0.38f
                : 0.31f;

        DrawTransformSnapToolbarBand(
            drawList,
            new Vector2(innerMin.X, innerMin.Y),
            new Vector2(innerMin.X + bandWidth, innerMax.Y),
            GetGizmoModeAccentColor(GizmoTransformMode.Translation),
            TransformSnapSettings.PositionEnabled,
            peakAlpha);

        var centerMinX = innerMin.X + bandWidth + bandGap;
        DrawTransformSnapToolbarBand(
            drawList,
            new Vector2(centerMinX, innerMin.Y),
            new Vector2(centerMinX + bandWidth, innerMax.Y),
            GetGizmoModeAccentColor(GizmoTransformMode.Rotation),
            TransformSnapSettings.RotationEnabled,
            peakAlpha);

        var rightMinX = centerMinX + bandWidth + bandGap;
        DrawTransformSnapToolbarBand(
            drawList,
            new Vector2(rightMinX, innerMin.Y),
            new Vector2(innerMax.X, innerMax.Y),
            GetGizmoModeAccentColor(GizmoTransformMode.Scale),
            TransformSnapSettings.ScaleEnabled,
            peakAlpha);
    }

    private static void DrawTransformSnapToolbarBand(ImDrawListPtr drawList, Vector2 min, Vector2 max, Vector4 accent, bool enabled, float peakAlpha)
    {
        if (!enabled || !EditorInputUtility.HasArea(min, max))
        {
            return;
        }

        var midY = min.Y + ((max.Y - min.Y) * 0.56f);
        var middleAlpha = EditorColors.WithAlpha(accent, peakAlpha);
        var topAlpha = EditorColors.WithAlpha(accent, peakAlpha * 0.22f);
        var fadeAlpha = EditorColors.WithAlpha(accent, peakAlpha * 0.18f);

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
        => DrawToolbarTooltip(EditorColors.AccentPurple, () =>
        {
            ImGui.TextColored(EditorColors.AccentPurple, "Transform Snap");
            ImGui.TextDisabled("Left click to toggle all snap modes.");
            ImGui.TextDisabled("Right click to open snap options.");
            ImGui.Spacing();
            ImGui.TextUnformatted(BuildTransformSnapTooltipLine("Position", TransformSnapSettings.PositionEnabled, TransformSnapSettings.PositionStep));
            ImGui.TextUnformatted(BuildTransformSnapTooltipLine("Rotation", TransformSnapSettings.RotationEnabled, TransformSnapSettings.RotationStepDegrees));
            ImGui.TextUnformatted(BuildTransformSnapTooltipLine("Scale", TransformSnapSettings.ScaleEnabled, TransformSnapSettings.ScaleStep));
            ImGui.TextUnformatted($"Dragging: {FormatOnOff(TransformSnapSettings.PositionDragEnabled)}");
            ImGui.Spacing();
            ImGui.TextDisabled("Off: hold Ctrl to apply snap");
            ImGui.TextDisabled("On: hold Ctrl to bypass snap");
        });

    private void DrawTransformSnapPopup(Vector2 anchorMin, Vector2 anchorMax)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var accent = EditorColors.AccentPurple;
        var defaults = DefaultTransformSnapSettings;
        var popupWidth = 300f * scale;
        ImGui.SetNextWindowPos(new Vector2(anchorMin.X, anchorMax.Y + (8f * scale)), ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(popupWidth, 0f), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new Vector2(popupWidth, 0f), new Vector2(popupWidth, float.MaxValue));

        using var windowPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(9f * scale, 9f * scale));
        using var windowRounding = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, 12f * scale);
        using var popupBg = ImRaii.PushColor(ImGuiCol.PopupBg, EditorColors.WithAlpha(_windowBodyBackgroundColor, 0.98f));
        using var popupBorder = ImRaii.PushColor(ImGuiCol.Border, EditorColors.WithAlpha(accent, 0.42f));

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

    private void DrawTransformSnapSectionCards(in ObjectTransformSnapSettings defaults)
    {
        ImGuiHelpers.ScaledDummy(TransformSnapPopupSectionSpacing);
        DrawTransformSnapSectionCard(
            "transformSnapPosition",
            FontAwesomeIcon.ArrowsAlt,
            "Position",
            GetGizmoModeAccentColor(GizmoTransformMode.Translation),
            TransformSnapSettings.PositionEnabled,
            TransformSnapSettings.PositionStep,
            defaults.PositionStep,
            "%.3f",
            0.001f,
            1000f,
            0.005f,
            step => TransformSnapSettings = TransformSnapSettings with { PositionStep = step });

        ImGuiHelpers.ScaledDummy(TransformSnapPopupSectionSpacing);
        DrawTransformSnapSectionCard(
            "transformSnapRotation",
            FontAwesomeIcon.SyncAlt,
            "Rotation",
            GetGizmoModeAccentColor(GizmoTransformMode.Rotation),
            TransformSnapSettings.RotationEnabled,
            TransformSnapSettings.RotationStepDegrees,
            defaults.RotationStepDegrees,
            "%.2f",
            0.01f,
            360f,
            0.10f,
            step => TransformSnapSettings = TransformSnapSettings with { RotationStepDegrees = step });

        ImGuiHelpers.ScaledDummy(TransformSnapPopupSectionSpacing);
        DrawTransformSnapSectionCard(
            "transformSnapScale",
            FontAwesomeIcon.CompressArrowsAlt,
            "Scale",
            GetGizmoModeAccentColor(GizmoTransformMode.Scale),
            TransformSnapSettings.ScaleEnabled,
            TransformSnapSettings.ScaleStep,
            defaults.ScaleStep,
            "%.3f",
            0.001f,
            10f,
            0.005f,
            step => TransformSnapSettings = TransformSnapSettings with { ScaleStep = step });
    }

    private void DrawTransformSnapActionRow()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var spacing = 6f * scale;

        DrawTransformSnapModeToggleButton("transformSnapPositionToggle", FontAwesomeIcon.ArrowsAlt, GizmoTransformMode.Translation, TransformSnapSettings.PositionEnabled, "Toggle position snap");

        ImGui.SameLine(0f, spacing);
        DrawTransformSnapModeToggleButton("transformSnapRotationToggle", FontAwesomeIcon.SyncAlt, GizmoTransformMode.Rotation, TransformSnapSettings.RotationEnabled, "Toggle rotation snap");

        ImGui.SameLine(0f, spacing);
        DrawTransformSnapModeToggleButton("transformSnapScaleToggle", FontAwesomeIcon.CompressArrowsAlt, GizmoTransformMode.Scale, TransformSnapSettings.ScaleEnabled, "Toggle scale snap");

        ImGui.SameLine(0f, spacing);
        DrawTransformSnapDragToggleButton();

        ImGui.SameLine(0f, spacing);
        if (DrawTransformSnapActionButton("transformSnapReset", FontAwesomeIcon.Undo, EditorColors.DimRed, false, "Reset snap settings to defaults", useAccentWhenInactive: true))
        {
            TransformSnapSettings = DefaultTransformSnapSettings;
        }
    }

    private void DrawTransformSnapDragToggleButton()
    {
        if (!DrawTransformSnapActionButton(
                "transformSnapDraggingToggle",
                FontAwesomeIcon.LocationArrow,
                GetGizmoModeAccentColor(GizmoTransformMode.Translation),
                TransformSnapSettings.PositionDragEnabled,
                "Toggle snapping for object dragging (based on position snap value)"))
        {
            return;
        }

        TransformSnapSettings = TransformSnapSettings with
        {
            PositionDragEnabled = !TransformSnapSettings.PositionDragEnabled,
        };
    }

    private void DrawTransformSnapModeToggleButton(string id, FontAwesomeIcon icon, GizmoTransformMode mode, bool enabled, string tooltip)
    {
        if (!DrawTransformSnapActionButton(id, icon, GetGizmoModeAccentColor(mode), enabled, tooltip))
        {
            return;
        }

        TransformSnapSettings = mode switch
        {
            GizmoTransformMode.Translation => TransformSnapSettings with { PositionEnabled = !TransformSnapSettings.PositionEnabled },
            GizmoTransformMode.Rotation => TransformSnapSettings with { RotationEnabled = !TransformSnapSettings.RotationEnabled },
            GizmoTransformMode.Scale => TransformSnapSettings with { ScaleEnabled = !TransformSnapSettings.ScaleEnabled },
            _ => TransformSnapSettings,
        };
    }

    private void DrawTransformSnapSectionCard(
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
        var iconSize = MeasureToolbarIcon(icon);
        var iconColumnWidth = MathF.Max(24f * scale, iconSize.X + (8f * scale));

        DrawPanelCard(
            id,
            EditorColors.WithAlpha(EditorColors.ButtonDefault, enabled ? 0.28f : 0.18f),
            EditorColors.WithAlpha(accent, enabled ? 0.42f : 0.20f),
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
                    using (ImRaii.PushFont(UiBuilder.IconFont))
                    {
                        var drawList = ImGui.GetWindowDrawList();
                        var iconText = icon.ToIconString();
                        var iconAreaMin = ImGui.GetCursorScreenPos();
                        var iconAreaSize = new Vector2(iconColumnWidth - (2f * scale), ImGui.GetFrameHeight());

                        ImGui.Dummy(iconAreaSize);
                        drawList.AddText(
                            new Vector2(
                                iconAreaMin.X + ((iconAreaSize.X - iconSize.X) * 0.5f),
                                iconAreaMin.Y + ((iconAreaSize.Y - iconSize.Y) * 0.5f)),
                            ImGui.GetColorU32(accent),
                            iconText);
                    }

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
                    using var border = ImRaii.PushColor(ImGuiCol.Border, EditorColors.WithAlpha(accent, 0.90f));
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

    private void DrawTransformSnapFooter()
    {
        ImGui.TextDisabled("Off: hold Ctrl to apply snap.");
        ImGui.TextDisabled("On: hold Ctrl to bypass snap.");
    }

    private static bool DrawTransformSnapActionButton(string id, FontAwesomeIcon icon, Vector4 accent, bool selected, string tooltip, bool useAccentWhenInactive = false)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var iconText = icon.ToIconString();
        var metrics = ResolveSquareIconButtonMetrics(iconText);
        var edge = MathF.Max(30f * scale, metrics.Edge);
        var size = new Vector2(edge, edge);
        var inactiveAccent = EditorColors.AccentGrey;
        var currentAccent = selected || useAccentWhenInactive
            ? accent
            : inactiveAccent;
        var fill = selected
            ? EditorColors.WithAlpha(currentAccent, 0.20f)
            : EditorColors.WithAlpha(EditorColors.ButtonDefault, 0.80f);
        var hoverFill = selected
            ? EditorColors.WithAlpha(currentAccent, 0.30f)
            : EditorColors.WithAlpha(currentAccent, useAccentWhenInactive ? 0.12f : 0.08f);
        var activeFill = selected
            ? EditorColors.WithAlpha(currentAccent, 0.36f)
            : EditorColors.WithAlpha(currentAccent, useAccentWhenInactive ? 0.18f : 0.12f);
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

        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(EditorColors.WithAlpha(currentAccent, selected ? 0.88f : useAccentWhenInactive ? 0.42f : 0.28f)),
            rounding,
            ImDrawFlags.None,
            MathF.Max(1f * scale, 1f));

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            drawList.AddText(
                new Vector2(
                    min.X + ((size.X - metrics.IconSize.X) * 0.5f),
                    min.Y + ((size.Y - metrics.IconSize.Y) * 0.5f)),
                ImGui.GetColorU32(EditorColors.WithAlpha(currentAccent, selected ? 1f : useAccentWhenInactive ? 0.88f : 0.72f)),
                iconText);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }

        return clicked;
    }

    private bool AreAnyTransformSnapModesEnabled()
        => TransformSnapSettings.PositionEnabled
           || TransformSnapSettings.RotationEnabled
           || TransformSnapSettings.ScaleEnabled;

    private bool AreAllTransformSnapModesEnabled()
        => TransformSnapSettings.PositionEnabled
           && TransformSnapSettings.RotationEnabled
           && TransformSnapSettings.ScaleEnabled;

    private void ToggleAllTransformSnapModes()
    {
        var enableAll = !AreAllTransformSnapModesEnabled();
        TransformSnapSettings = TransformSnapSettings with
        {
            PositionEnabled = enableAll,
            RotationEnabled = enableAll,
            ScaleEnabled = enableAll,
        };
    }

    private static string BuildTransformSnapTooltipLine(string label, bool enabled, float step)
        => $"{label}: {FormatOnOff(enabled)} ({FormatTransformSnapStep(step)})";

    private static string FormatTransformSnapStep(float step)
        => step.ToString("0.###");

    private static bool AreTransformSnapStepsEqual(float left, float right)
        => ObjectMathUtility.IsNearlyEqual(left, right, TransformSnapStepEqualityTolerance);

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
        using var button = ImRaii.PushColor(ImGuiCol.Button, EditorColors.WithAlpha(EditorColors.ButtonDefault, 0.82f));
        using var hovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, EditorColors.WithAlpha(accent, 0.14f));
        using var active = ImRaii.PushColor(ImGuiCol.ButtonActive, EditorColors.WithAlpha(accent, 0.20f));
        using var border = ImRaii.PushColor(ImGuiCol.Border, EditorColors.WithAlpha(accent, 0.56f));
        using var borderSize = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, MathF.Max(1f * ImGuiHelpers.GlobalScale, 1f));
        using var frameRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 6f * ImGuiHelpers.GlobalScale);
        return ImGui.Button($"{label}{id}", size);
    }

    private static bool IsTransformSnapPopupOpen()
        => ImGui.IsPopupOpen(TransformSnapPopupId);

    private void ApplyInspectorTransformEdit(string editId, string title, ObjectHistoryKind kind, ObjectSnapshot snapshot, ObjectTransform nextTransform)
    {
        if (Equals(snapshot.Transform, nextTransform))
        {
            return;
        }

        ApplyInspectorSnapshotEdit(
            editId,
            kind,
            title,
            snapshot,
            snapshot with { Transform = nextTransform });
    }

    private void ApplyInspectorPositionEdit(string editId, string title, ObjectHistoryKind kind, ObjectSnapshot snapshot, Vector3 position)
    {
        var nextTransform = snapshot.Transform with { Position = ApplyPositionSnap(position, WorldTransformSnapBasis) };
        ApplyInspectorTransformEdit(editId, title, kind, snapshot, nextTransform);
    }

    private void ApplyInspectorRotationEdit(string editId, string title, ObjectHistoryKind kind, ObjectSnapshot snapshot, Vector3 rotationDegrees)
    {
        var nextTransform = snapshot.Transform with { RotationDegrees = ApplyRotationSnap(rotationDegrees) };
        ApplyInspectorTransformEdit(editId, title, kind, snapshot, nextTransform);
    }

    private void ApplyInspectorScaleEdit(string editId, string title, ObjectHistoryKind kind, ObjectSnapshot snapshot, Vector3 scale)
    {
        var nextTransform = snapshot.Transform with { Scale = ApplyScaleSnap(scale) };
        ApplyInspectorTransformEdit(editId, title, kind, snapshot, nextTransform);
    }
}

