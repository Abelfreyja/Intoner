using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Intoner.Objects.Models;
using Intoner.Objects.Rendering;
using Intoner.Objects.Rendering.Drawing;
using Intoner.Objects.UI.Components;
using Intoner.Objects.Utils;
using Intoner.Services.Configuration;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class Gizmo
{
    private void DrawLinearGizmo(
        in GizmoFrame frame,
        GizmoTransformMode operation,
        ReadOnlySpan<GizmoAxisVisualState> axes,
        DrawBatch batch,
        ImDrawListPtr drawList,
        in GizmoDrawOptions options)
    {
        GizmoInteractionState common = frame.ForOperation(operation);
        foreach (GizmoAxisVisualState state in axes)
        {
            if (common.DragActive && state.Axis != common.ActiveAxis)
            {
                if (operation == GizmoTransformMode.Translation)
                {
                    DrawSuppressedGizmoAxis(batch, state);
                }
                else
                {
                    DrawScaleGizmoStem(batch, frame, state, GizmoInactiveColor);
                    Vector2 half = new(GizmoConstants.ScaleHandleSize * state.VisualScale * 0.5f);
                    batch.AddScreenRect(state.ScreenEnd - half, state.ScreenEnd + half, GizmoInactiveColor, state.VisualScale);
                }

                continue;
            }

            bool active = common.DragActive && state.Axis == common.ActiveAxis;
            bool hovered = common.Phase == GizmoInteractionPhase.HoverAxis && state.Axis == frame.HoveredHandle.Axis;
            Vector4 color = active || hovered && _appearance.Preset != GizmoColorPreset.Default
                ? GizmoHighlightColor : GetAxisColorVector(state.Axis, active, hovered);
            Vector4 glow = active || hovered ? color : GetGizmoAxisBase(state.Axis);
            glow.W = active ? 0.45f : 0.25f;
            float opacity = ResolveGizmoHandleOpacity(frame, operation, state.Axis);
            color.W *= opacity;
            glow.W *= opacity;

            float visualScale = state.VisualScale;
            Vector2 end = operation == GizmoTransformMode.Translation ? GetTrimmedGizmoEndpoint(state, visualScale) : state.ScreenEnd;
            if (operation == GizmoTransformMode.Scale && frame.IsCombined)
            {
                DrawScaleGizmoStem(batch, frame, state, color);
            }
            else
            {
                batch.AddScreenLine(state.ScreenStart, end, glow,
                    GizmoConstants.AxisLineThickness * visualScale * GizmoConstants.AxisGlowThicknessMultiplier);
                batch.AddScreenLine(state.ScreenStart, end, color, GizmoConstants.AxisLineThickness * visualScale);
            }

            if (operation == GizmoTransformMode.Scale)
            {
                DrawScaleHandle(batch, state.ScreenEnd, visualScale, color, active, hovered);
            }
            else
            {
                DrawAxisArrowhead(batch, state, visualScale, color, opacity);
            }

            if (active || !frame.IsCombined && !hovered)
            {
                DrawAxisLabel(drawList, state, visualScale, color, AxisLabel(state.Axis), active, opacity, options);
            }
        }
    }

    private void DrawRotationGizmo(in GizmoFrame frame, DrawBatch batch, float scale)
    {
        GizmoContext context = frame.Context;
        RotationProjectionContext rotationProjection = frame.RotationProjection;
        GizmoInteractionState common = frame.ForOperation(GizmoTransformMode.Rotation);
        RotationHoverState hoverState = frame.HoveredHandle.Rotation;
        DrawRotationAxes(batch, frame, scale, common, RotationAxisSegmentPass.Hidden);

        if (!frame.IsCombined)
        {
            var radius = rotationProjection.VisualRadius + (GizmoConstants.RotationRingThickness * scale);
            batch.AddScreenCircleFilled(context.ScreenPos, radius,
                ThemeColors.Color(0f, 0f, 0f, GizmoConstants.RotationBackgroundAlpha * ResolveGizmoHoverOpacity(frame.Interaction, false)), 96);
        }

        DrawRotationAxes(batch, frame, scale, common, RotationAxisSegmentPass.Visible);

        if (common.DragActive)
        {
            DrawRotationDragHighlight(batch, rotationProjection, scale);
        }

        var snapPolicy = ResolveActiveTransformSnapPolicy(context);
        if (snapPolicy.RotationEnabled && snapPolicy.RotationStepDegrees > 0f)
        {
            GizmoAxis tickAxis = GizmoAxis.None;
            if (common.DragActive)
            {
                tickAxis = common.ActiveAxis;
            }
            else if (common.Phase == GizmoInteractionPhase.HoverAxis)
            {
                tickAxis = hoverState.Axis;
            }

            if (tickAxis != GizmoAxis.None)
            {
                var tickAngleOffset = common.DragActive && RotationDragState.RotationDragStartAngle.HasValue
                    ? RotationDragState.RotationDragStartAngle.Value
                    : 0f;
                DrawRotationSnapTicks(batch, rotationProjection, scale, tickAxis, snapPolicy.RotationStepDegrees, tickAngleOffset);
            }
        }

        if (common.Phase == GizmoInteractionPhase.HoverAxis && hoverState.HasPoint)
        {
            batch.AddScreenCircle(
                hoverState.ScreenPoint,
                GizmoConstants.RotationHoverIndicatorRadius * scale,
                Vector4.One,
                GizmoConstants.RotationHoverIndicatorThickness * scale,
                48);
        }
        else if (common.DragActive && RotationDragState.RotationDragStartAngle.HasValue)
        {
            var axisDirection = ResolveAxisWorldDirection(RotationDragState.ActiveAxis, rotationProjection.Rotation, rotationProjection.UseWorldSpace);
            var dragIndicator = GizmoRotationMath.ProjectAxisPoint(
                CreateRotationMathProjection(rotationProjection),
                axisDirection,
                GizmoRotationMath.NormalizeAngle(RotationDragState.RotationDragStartAngle.Value));
            batch.AddScreenCircleFilled(
                dragIndicator,
                GizmoConstants.RotationDragIndicatorRadius * scale,
                GetAxisColorVector(RotationDragState.ActiveAxis, true, false),
                48);
        }
    }

    private enum RotationAxisSegmentPass
    {
        Hidden,
        Visible,
    }

    private static string AxisLabel(GizmoAxis axis)
        => axis switch
        {
            GizmoAxis.X => "X",
            GizmoAxis.Y => "Y",
            GizmoAxis.Z => "Z",
            _ => string.Empty,
        };

    private static bool HasActiveModifierIndicators()
        => GizmoInputUtility.HasActiveModifierIndicators();

    private static bool IsSlowDragModifierActive()
        => GizmoInputUtility.IsSlowDragModifierActive();

    private static bool IsPrecisionSnapModifierActive()
        => GizmoInputUtility.IsPrecisionSnapModifierActive();

    private static float GetModifierIndicatorFontSize()
        => GizmoInputUtility.GetModifierIndicatorFontSize();

    private void DrawGizmoDragMetrics(Vector2 referencePosition)
    {
        if (!TryBuildGizmoMetricInfo(out var metricInfo))
        {
            return;
        }

        var text = metricInfo.Text;
        var drawList = ImGui.GetForegroundDrawList();
        var scale = ImGuiHelpers.GlobalScale;
        var padding = new Vector2(6f * scale, 4f * scale);
        var textSize = ImGui.CalcTextSize(text);
        var modifierHeight = HasActiveModifierIndicators()
            ? (GetModifierIndicatorFontSize() + (3f * scale))
            : 0f;
        var boxSize = textSize + (padding * 2f) + new Vector2(0f, modifierHeight);
        var position = ResolveDragMetricsPosition(referencePosition, boxSize, scale);
        var rectMin = position;
        var rectMax = position + boxSize;

        drawList.AddRectFilled(rectMin, rectMax, ImGui.GetColorU32(ThemeColors.Color(0f, 0f, 0f, 0.55f)), 4f * scale);
        var textPos = rectMin + padding;
        drawList.AddText(textPos, ImGui.GetColorU32(ImGuiCol.Text), text);
        DrawModifierIndicatorRow(drawList, textPos, textSize, scale);
    }

    private void DrawTranslationDragPath(in GizmoContext context, float scale)
    {
        var batch = _drawManager.BeginPass(DrawPassKind.GizmoDragPath, "Gizmo Drag Path", DrawLayer.Foreground);
        batch.AddLine(
            TranslationDragState.StartPosition,
            context.PivotPosition,
            EditorColors.GizmoTranslationDragPath,
            GizmoConstants.TranslationDragPathThickness * scale);
        batch.AddPoint(
            TranslationDragState.StartPosition,
            EditorColors.GizmoTranslationDragPath,
            GizmoConstants.TranslationDragStartMarkerRadius * scale);
    }

    private void DrawSuppressedGizmoAxis(DrawBatch batch, GizmoAxisVisualState state)
    {
        batch.AddScreenLine(
            state.ScreenStart,
            state.ScreenEnd,
            GizmoInactiveColor,
            GizmoConstants.AxisLineThickness * state.VisualScale * 0.75f);
    }

    private void DrawScaleGizmoStem(DrawBatch batch, in GizmoFrame frame, GizmoAxisVisualState state, Vector4 color)
    {
        float scale = state.VisualScale;
        if (!frame.IsCombined)
        {
            batch.AddScreenLine(state.ScreenStart, state.ScreenEnd, color, scale);
            return;
        }

        float start = GetCombinedScaleStemStart(state, frame.Modes, frame.Context.UseWorldSpace, ImGuiHelpers.GlobalScale);
        float stop = state.ScreenLength - GizmoConstants.ScaleHandleSize * scale * 0.5f;
        bool hovered = frame.Interaction.Phase == GizmoInteractionPhase.HoverAxis
            && frame.HoveredHandle.Operation == GizmoTransformMode.Scale && frame.HoveredHandle.Axis == state.Axis;
        if (hovered)
        {
            float innerStart = ResolveCenterInteractionRadius(ImGuiHelpers.GlobalScale) + 5f * scale;
            start -= MathF.Floor(MathF.Max(0f, start - innerStart) / (8f * scale)) * (8f * scale);
        }

        for (float offset = start; offset < stop; offset += 8f * scale)
        {
            Vector2 dashStart = state.ScreenStart + state.ScreenDirection * offset;
            Vector2 dashEnd = state.ScreenStart + state.ScreenDirection * MathF.Min(offset + 4f * scale, stop);
            Vector4 dashColor = color;
            if (hovered)
            {
                GizmoHandleHit hit = default;
                FindLinearHandle(State.TranslationAxes.AsSpan(0, frame.TranslationAxisCount), GizmoTransformMode.Translation,
                    frame.Modes, frame.Context.UseWorldSpace, (dashStart + dashEnd) * 0.5f, ImGuiHelpers.GlobalScale, ref hit);
                if (hit.IsValid)
                {
                    dashColor = GizmoHighlightColor with { W = color.W };
                }
            }

            batch.AddScreenLine(dashStart, dashEnd, dashColor, scale);
        }
    }

    private static void DrawAxisArrowhead(DrawBatch batch, GizmoAxisVisualState state, float scale, Vector4 axisColor, float opacity)
    {
        if (!NumericsUtility.TryNormalize(state.ScreenDirection, out var direction))
        {
            return;
        }

        var normal = new Vector2(-direction.Y, direction.X);
        var arrowLength = GizmoConstants.AxisArrowLength * scale;
        var arrowWidth = GizmoConstants.AxisArrowWidth * scale;
        var tip = state.ScreenEnd;
        var basePoint = tip - (direction * arrowLength);
        var left = basePoint + (normal * arrowWidth);
        var right = basePoint - (normal * arrowWidth);

        batch.AddScreenTriangle(tip, left, right, axisColor);
        var outlineColor = ThemeColors.Color(axisColor.X, axisColor.Y, axisColor.Z, MathF.Min(axisColor.W + 0.15f * opacity, opacity));
        batch.AddScreenLine(tip, left, outlineColor, 1.1f * scale);
        batch.AddScreenLine(left, right, outlineColor, 1.1f * scale);
        batch.AddScreenLine(right, tip, outlineColor, 1.1f * scale);
    }

    private static void DrawScaleHandle(DrawBatch batch, Vector2 position, float scale, Vector4 axisColor, bool isActive, bool isHovered)
    {
        var half = new Vector2(GizmoConstants.ScaleHandleSize * scale * 0.5f);
        var highlight = isActive || isHovered;
        var fillColor = ThemeColors.Color(axisColor.X, axisColor.Y, axisColor.Z, axisColor.W * (highlight ? 0.95f : 0.65f));
        batch.AddScreenRectFilled(position - half, position + half, fillColor);
        batch.AddScreenRect(position - half, position + half, ThemeColors.Color(axisColor.X, axisColor.Y, axisColor.Z, axisColor.W * 0.95f), 1f * scale);
    }

    private static void DrawAxisLabel(
        ImDrawListPtr drawList,
        GizmoAxisVisualState state,
        float scale,
        Vector4 axisColor,
        string label,
        bool isActive,
        float opacity,
        in GizmoDrawOptions options)
    {
        if (string.IsNullOrEmpty(label))
        {
            return;
        }

        var direction = !NumericsUtility.TryNormalize(state.ScreenDirection, out var normalizedDirection)
            ? Vector2.UnitX
            : normalizedDirection;
        var center = state.ScreenEnd + (direction * GizmoConstants.AxisLabelDistance * scale);
        var textSize = ImGui.CalcTextSize(label);
        var padding = new Vector2(GizmoConstants.AxisLabelPadding * scale);
        var rectMin = center - (textSize * 0.5f) - padding;
        var rectMax = center + (textSize * 0.5f) + padding;
        if (!options.CanDrawLabel(rectMin, rectMax))
        {
            return;
        }

        var backgroundColor = ThemeColors.Color(axisColor.X, axisColor.Y, axisColor.Z, isActive ? 0.95f : 0.70f);
        var textColor = isActive
            ? ThemeColors.Color(0f, 0f, 0f, 0.95f)
            : ThemeColors.Color(0.05f, 0.05f, 0.05f, 0.90f);

        drawList.AddRectFilled(rectMin, rectMax, ImGui.GetColorU32(backgroundColor with { W = backgroundColor.W * opacity }), GizmoConstants.AxisLabelRoundness * scale);
        drawList.AddRect(rectMin, rectMax, ImGui.GetColorU32(ThemeColors.Color(axisColor.X, axisColor.Y, axisColor.Z, 0.95f * opacity)), GizmoConstants.AxisLabelRoundness * scale, ImDrawFlags.None, 1.05f * scale);
        drawList.AddText(center - (textSize * 0.5f), ImGui.GetColorU32(textColor with { W = textColor.W * opacity }), label);
    }

    private void DrawCircularCenterHandle(DrawBatch batch, Vector2 screenPos, float scale, bool isHovered, bool isActive, bool alignToSurfaceNormal, float opacity)
    {
        var radius = GizmoConstants.CenterPointRadius * scale;
        float fillOpacity = 0.95f;
        if (isActive)
        {
            fillOpacity = 1f;
        }
        else if (isHovered)
        {
            fillOpacity = 0.98f;
        }

        Vector4 fillColor = _appearance.Preset != GizmoColorPreset.Default && (isHovered || isActive)
            ? GizmoHighlightColor : GizmoCenterColor;
        fillColor.W *= fillOpacity * opacity;
        batch.AddScreenCircleFilled(screenPos, radius, fillColor, 32);

        if (!isHovered && !isActive)
        {
            return;
        }

        Vector4 accentColor = GizmoHighlightColor;
        if (_appearance.Preset == GizmoColorPreset.Default)
        {
            accentColor = alignToSurfaceNormal ? ThemeColors.AccentGreen : ThemeColors.AccentBlue;
        }
        accentColor.W = isActive ? 0.95f : 0.80f;
        batch.AddScreenCircle(
            screenPos,
            ResolveCenterInteractionRadius(scale),
            accentColor,
            MathF.Max(1.4f * scale, 1f),
            48);
    }

    private static string GetGizmoHandleLabel(GizmoTransformMode operation, GizmoAxis axis, bool world)
        => (operation, axis, world) switch
        {
            (GizmoTransformMode.Translation, GizmoAxis.X, true) => "Move X (World)",
            (GizmoTransformMode.Translation, GizmoAxis.Y, true) => "Move Y (World)",
            (GizmoTransformMode.Translation, GizmoAxis.Z, true) => "Move Z (World)",
            (GizmoTransformMode.Translation, GizmoAxis.X, false) => "Move X (Local)",
            (GizmoTransformMode.Translation, GizmoAxis.Y, false) => "Move Y (Local)",
            (GizmoTransformMode.Translation, GizmoAxis.Z, false) => "Move Z (Local)",
            (GizmoTransformMode.Rotation, GizmoAxis.X, true) => "Rotate X (World)",
            (GizmoTransformMode.Rotation, GizmoAxis.Y, true) => "Rotate Y (World)",
            (GizmoTransformMode.Rotation, GizmoAxis.Z, true) => "Rotate Z (World)",
            (GizmoTransformMode.Rotation, GizmoAxis.X, false) => "Rotate X (Local)",
            (GizmoTransformMode.Rotation, GizmoAxis.Y, false) => "Rotate Y (Local)",
            (GizmoTransformMode.Rotation, GizmoAxis.Z, false) => "Rotate Z (Local)",
            (GizmoTransformMode.Scale, GizmoAxis.X, _) => "Scale X (Local)",
            (GizmoTransformMode.Scale, GizmoAxis.Y, _) => "Scale Y (Local)",
            (GizmoTransformMode.Scale, GizmoAxis.Z, _) => "Scale Z (Local)",
            _ => string.Empty,
        };

    private static void DrawGizmoHandleLabel(ImDrawListPtr drawList, in GizmoFrame frame, float scale, in GizmoDrawOptions options)
    {
        string label = GetGizmoHandleLabel(frame.HoveredHandle.Operation, frame.HoveredHandle.Axis, frame.Context.UseWorldSpace);
        if (!TryGetGizmoHandleLabelPosition(frame, ImGui.CalcTextSize(label), scale, options, out Vector2 position))
        {
            return;
        }

        drawList.AddText(position + new Vector2(scale), ImGui.GetColorU32(ThemeColors.Color(0f, 0f, 0f, 0.85f)), label);
        drawList.AddText(position, ImGui.GetColorU32(ThemeColors.Text), label);
    }

    private static bool TryGetGizmoHandleLabelPosition(
        in GizmoFrame frame,
        Vector2 textSize,
        float scale,
        in GizmoDrawOptions options,
        out Vector2 position)
    {
        float gap = 8f * scale;
        Vector2 margin = new(gap);
        GizmoContext context = frame.Context;
        EditorScreenArea viewport = new(context.ViewportPos + margin, context.ViewportPos + context.ViewportSize - margin);
        position = default;
        if (textSize.X > viewport.Max.X - viewport.Min.X || textSize.Y > viewport.Max.Y - viewport.Min.Y)
        {
            return false;
        }

        Vector2 anchor = ImGui.GetIO().MousePos;
        float right = anchor.X + 12f * scale;
        float left = anchor.X - 12f * scale - textSize.X;
        float above = anchor.Y - gap - textSize.Y;
        float below = anchor.Y + gap;
        ReadOnlySpan<Vector2> candidates = stackalloc Vector2[] { new(right, above), new(left, above), new(right, below), new(left, below) };
        foreach (Vector2 candidate in candidates)
        {
            if (viewport.Contains(candidate) && viewport.Contains(candidate + textSize) && options.CanDrawLabel(candidate, candidate + textSize))
            {
                position = candidate;
                return true;
            }
        }

        return false;
    }

    private void DrawGizmoLabel(
        ImDrawListPtr drawList,
        in GizmoContext context,
        float scale,
        in GizmoDrawOptions options)
    {
        var modeLabel = Mode switch
        {
            GizmoTransformMode.Translation => "Move",
            GizmoTransformMode.Rotation => "Rotate",
            GizmoTransformMode.Scale => "Scale",
            GizmoTransformMode.Translation | GizmoTransformMode.Rotation => "Move + Rotate",
            GizmoTransformMode.Translation | GizmoTransformMode.Scale => "Move + Scale",
            GizmoTransformMode.Rotation | GizmoTransformMode.Scale => "Rotate + Scale",
            GizmoTransformMode.Universal => "Universal",
            _ => string.Empty,
        };

        var labelRoot = context.SelectionCount == 1
            ? context.PrimarySnapshot.Name
            : $"{context.SelectionCount} items";
        var label = string.IsNullOrEmpty(modeLabel)
            ? labelRoot
            : $"{labelRoot} [{modeLabel}]";
        var textSize = ImGui.CalcTextSize(label);
        var textPos = context.ScreenPos + new Vector2((GizmoConstants.CenterPointRadius * scale) + (6f * scale), -(textSize.Y * 0.5f));
        if (!options.CanDrawLabel(textPos, textPos + textSize))
        {
            return;
        }

        drawList.AddText(textPos, ImGui.GetColorU32(ImGuiCol.Text), label);
    }

    private static float ResolveRotationInteractionRadius(in RotationProjectionContext projection, float scale)
        => projection.VisualRadius + (GizmoConstants.RotationInteractionPadding * scale);

    private void DrawRotationAxes(
        DrawBatch batch,
        in GizmoFrame frame,
        float scale,
        in GizmoInteractionState common,
        RotationAxisSegmentPass pass)
    {
        for (var index = 0; index < GizmoAxisUtility.AxisCount; ++index)
        {
            var axis = GizmoAxisUtility.FromIndex(index);
            if (common.DragActive && axis != common.ActiveAxis)
            {
                continue;
            }

            DrawRotationAxisPath(
                batch,
                frame.RotationProjection,
                GizmoConstants.RotationRingThickness * scale,
                axis,
                isActive: common.DragActive && axis == common.ActiveAxis,
                isHovered: common.Phase == GizmoInteractionPhase.HoverAxis && axis == frame.HoveredHandle.Axis,
                pass,
                ResolveGizmoHandleOpacity(frame, GizmoTransformMode.Rotation, axis));
        }
    }

    private void DrawRotationAxisPath(
        DrawBatch batch,
        in RotationProjectionContext projection,
        float thickness,
        GizmoAxis axis,
        bool isActive,
        bool isHovered,
        RotationAxisSegmentPass pass,
        float opacity)
    {
        var axisDirection = ResolveAxisWorldDirection(axis, projection.Rotation, projection.UseWorldSpace);
        var rotationMathProjection = CreateRotationMathProjection(projection);
        Span<Vector2> projectedPoints = stackalloc Vector2[GizmoConstants.RotationRingSegments + 1];
        Span<bool> validPoints = stackalloc bool[GizmoConstants.RotationRingSegments + 1];
        Span<bool> visiblePoints = stackalloc bool[GizmoConstants.RotationRingSegments + 1];

        for (var index = 0; index <= GizmoConstants.RotationRingSegments; ++index)
        {
            var angle = index / (float)GizmoConstants.RotationRingSegments * MathF.Tau;
            validPoints[index] = GizmoRotationMath.TryProjectAxisPoint(
                rotationMathProjection, axisDirection, angle, out projectedPoints[index], out visiblePoints[index]);
        }

        Vector4 color = pass == RotationAxisSegmentPass.Visible
            ? GetAxisColorVector(axis, isActive, isHovered)
            : GetAxisBackgroundColorVector(axis, isActive);
        color.W *= opacity;
        for (var segment = 1; segment <= GizmoConstants.RotationRingSegments; ++segment)
        {
            if (!ShouldDrawRotationSegment(validPoints, visiblePoints, segment, pass))
            {
                continue;
            }

            var caps = ResolveRotationSegmentCaps(validPoints, visiblePoints, segment, pass);
            AddJoinedScreenLine(
                batch,
                projectedPoints,
                segment - 1,
                segment,
                segment == 1 ? GizmoConstants.RotationRingSegments - 1 : segment - 2,
                segment == GizmoConstants.RotationRingSegments ? 1 : segment + 1,
                color,
                thickness,
                caps);
        }
    }

    private static bool ShouldDrawRotationSegment(
        ReadOnlySpan<bool> validPoints,
        ReadOnlySpan<bool> visiblePoints,
        int segment,
        RotationAxisSegmentPass pass)
        => validPoints[segment - 1] && validPoints[segment]
            && ((pass == RotationAxisSegmentPass.Visible) == (visiblePoints[segment - 1] && visiblePoints[segment]));

    private static ScreenLineCaps ResolveRotationSegmentCaps(
        ReadOnlySpan<bool> validPoints,
        ReadOnlySpan<bool> visiblePoints,
        int segment,
        RotationAxisSegmentPass pass)
    {
        var caps = ScreenLineCaps.Both;
        var previousSegment = segment == 1
            ? GizmoConstants.RotationRingSegments
            : segment - 1;
        var nextSegment = segment == GizmoConstants.RotationRingSegments
            ? 1
            : segment + 1;

        if (ShouldDrawRotationSegment(validPoints, visiblePoints, previousSegment, pass))
        {
            caps &= ~ScreenLineCaps.Start;
        }

        if (ShouldDrawRotationSegment(validPoints, visiblePoints, nextSegment, pass))
        {
            caps &= ~ScreenLineCaps.End;
        }

        return caps;
    }

    private void DrawRotationDragHighlight(
        DrawBatch batch,
        in RotationProjectionContext projection,
        float scale)
    {
        if (!RotationDragState.RotationDragStartAngle.HasValue || RotationDragState.ActiveAxis == GizmoAxis.None)
        {
            return;
        }

        var deltaRadians = RotationDragState.RotationDragAppliedRadians;
        if (NumericsUtility.IsNearlyZero(deltaRadians, 0.0001f))
        {
            return;
        }

        var steps = Math.Clamp((int)(MathF.Abs(deltaRadians) / (MathF.PI * 2f) * GizmoConstants.RotationRingSegments), 4, GizmoConstants.RotationRingSegments);
        var axisDirection = ResolveAxisWorldDirection(RotationDragState.ActiveAxis, projection.Rotation, projection.UseWorldSpace);
        var rotationMathProjection = CreateRotationMathProjection(projection);
        Vector4 highlightColor = _appearance.Preset == GizmoColorPreset.Default
            ? EditorColors.GizmoRotationDragHighlight : GizmoHighlightColor;
        var fillColor = ThemeColors.WithAlpha(highlightColor, GizmoConstants.RotationHighlightSectorFillAlpha);
        var boundaryColor = ThemeColors.WithAlpha(highlightColor, GizmoConstants.RotationHighlightSectorBoundaryAlpha);
        var arcThickness = GizmoConstants.RotationRingThickness * scale * GizmoConstants.RotationHighlightThicknessMultiplier;
        var boundaryThickness = GizmoConstants.AxisLineThickness * scale * GizmoConstants.RotationHighlightSectorBoundaryThicknessMultiplier;
        Span<Vector2> projectedPoints = stackalloc Vector2[steps + 1];
        Span<bool> validPoints = stackalloc bool[steps + 1];
        for (var index = 0; index <= steps; ++index)
        {
            var t = index / (float)steps;
            var angle = RotationDragState.RotationDragStartAngle.Value + (deltaRadians * t);
            validPoints[index] = GizmoRotationMath.TryProjectAxisPoint(
                rotationMathProjection, axisDirection, GizmoRotationMath.NormalizeAngle(angle), out projectedPoints[index], out _);
        }

        for (var segment = 1; segment <= steps; ++segment)
        {
            if (!validPoints[segment - 1] || !validPoints[segment])
            {
                continue;
            }

            var caps = ResolveOpenPathSegmentCaps(validPoints, segment);
            DrawRotationDragSectorFillSegment(batch, projection.Center, projectedPoints[segment - 1], projectedPoints[segment], fillColor);
            AddJoinedScreenLine(
                batch,
                projectedPoints,
                segment - 1,
                segment,
                segment - 2,
                segment + 1,
                highlightColor,
                arcThickness,
                caps);
        }

        DrawRotationDragSectorBoundary(
            batch,
            projection.Center,
            rotationMathProjection,
            axisDirection,
            RotationDragState.RotationDragStartAngle.Value,
            boundaryColor,
            boundaryThickness);
        DrawRotationDragSectorBoundary(
            batch,
            projection.Center,
            rotationMathProjection,
            axisDirection,
            RotationDragState.RotationDragStartAngle.Value + deltaRadians,
            boundaryColor,
            boundaryThickness);
    }

    private static ScreenLineCaps ResolveOpenPathSegmentCaps(ReadOnlySpan<bool> validPoints, int segment)
    {
        var caps = ScreenLineCaps.None;
        if (segment == 1 || !validPoints[segment - 2])
        {
            caps |= ScreenLineCaps.Start;
        }

        if (segment == validPoints.Length - 1 || !validPoints[segment + 1])
        {
            caps |= ScreenLineCaps.End;
        }

        return caps;
    }

    private static void AddJoinedScreenLine(
        DrawBatch batch,
        ReadOnlySpan<Vector2> points,
        int startIndex,
        int endIndex,
        int previousIndex,
        int nextIndex,
        Vector4 color,
        float thickness,
        ScreenLineCaps caps)
    {
        var previous = ScreenLineCapsUtility.Has(caps, ScreenLineCaps.Start)
            ? points[startIndex]
            : points[previousIndex];
        var next = ScreenLineCapsUtility.Has(caps, ScreenLineCaps.End)
            ? points[endIndex]
            : points[nextIndex];
        batch.AddScreenJoinedLine(previous, points[startIndex], points[endIndex], next, color, thickness, caps);
    }

    private static void DrawRotationDragSectorBoundary(
        DrawBatch batch,
        Vector2 center,
        in GizmoRotationMath.Projection projection,
        Vector3 axisDirection,
        float angle,
        Vector4 color,
        float thickness)
    {
        if (!GizmoRotationMath.TryProjectAxisPoint(
                projection,
                axisDirection,
                GizmoRotationMath.NormalizeAngle(angle),
                out var point,
                out _))
        {
            return;
        }

        batch.AddScreenLine(center, point, color, thickness);
    }

    private static void DrawRotationSnapTicks(
        DrawBatch batch,
        in RotationProjectionContext projection,
        float scale,
        GizmoAxis axis,
        float stepDegrees,
        float angleOffset)
    {
        if (stepDegrees <= 0f)
        {
            return;
        }

        var axisDirection = ResolveAxisWorldDirection(axis, projection.Rotation, projection.UseWorldSpace);
        var rotationMathProjection = CreateRotationMathProjection(projection);
        var stepRadians = stepDegrees * (MathF.PI / 180f);
        var tickCount = Math.Max(1, (int)MathF.Ceiling(MathF.Tau / stepRadians));
        var tickHalfLength = GizmoConstants.RotationSnapTickLength * scale * 0.5f;
        var majorTickHalfLength = GizmoConstants.RotationSnapMajorTickLength * scale * 0.5f;
        var tickThickness = GizmoConstants.RotationSnapTickThickness * scale;
        var visibleColor = ThemeColors.Color(0f, 0f, 0f, GizmoConstants.RotationSnapTickAlpha);
        var hiddenColor = ThemeColors.Color(0f, 0f, 0f, GizmoConstants.RotationSnapTickHiddenAlpha);
        var majorStepRadians = MathF.PI * 0.5f;

        for (var index = 0; index < tickCount; ++index)
        {
            var angle = angleOffset + (index * stepRadians);
            if (!GizmoRotationMath.TryProjectAxisPoint(
                    rotationMathProjection,
                    axisDirection,
                    GizmoRotationMath.NormalizeAngle(angle),
                    out var projected,
                    out var isVisible))
            {
                continue;
            }

            var radial = projected - projection.Center;
            if (!NumericsUtility.TryNormalize(radial, out var radialDirection))
            {
                continue;
            }

            var normalizedAngle = GizmoRotationMath.NormalizeAngle(angle - angleOffset);
            var majorStepIndex = normalizedAngle / majorStepRadians;
            var isMajorTick = NumericsUtility.IsNearlyEqual(majorStepIndex, MathF.Round(majorStepIndex), 0.001f);
            var currentTickHalfLength = isMajorTick ? majorTickHalfLength : tickHalfLength;
            batch.AddScreenLine(
                projected - (radialDirection * currentTickHalfLength),
                projected + (radialDirection * currentTickHalfLength),
                isVisible ? visibleColor : hiddenColor,
                tickThickness);
        }
    }

    private static void DrawRotationDragSectorFillSegment(DrawBatch batch, Vector2 center, Vector2 start, Vector2 end, Vector4 color)
    {
        var signedArea = ((start.X - center.X) * (end.Y - center.Y)) - ((start.Y - center.Y) * (end.X - center.X));
        if (signedArea < 0f)
        {
            (start, end) = (end, start);
        }

        batch.AddScreenTriangle(center, start, end, color);
    }

    private static void DrawModifierIndicatorRow(ImDrawListPtr drawList, Vector2 textPos, Vector2 textSize, float scale)
    {
        if (!HasActiveModifierIndicators())
        {
            return;
        }

        var iconPosition = new Vector2(textPos.X, textPos.Y + textSize.Y + (3f * scale));
        var iconSize = GetModifierIndicatorFontSize();
        var iconSpacing = iconSize + (4f * scale);

        if (IsSlowDragModifierActive())
        {
            DrawModifierIndicatorIcon(drawList, iconPosition, iconSize, FontAwesomeIcon.SyncAlt.ToIconString());
            iconPosition.X += iconSpacing;
        }

        if (IsPrecisionSnapModifierActive())
        {
            DrawModifierIndicatorIcon(drawList, iconPosition, iconSize, FontAwesomeIcon.Crosshairs.ToIconString());
        }
    }

    private static void DrawModifierIndicatorIcon(ImDrawListPtr drawList, Vector2 position, float fontSize, string icon)
    {
        drawList.AddText(UiBuilder.IconFont, fontSize, position, ImGui.GetColorU32(ThemeColors.Color(1f, 1f, 1f, 0.55f)), icon);
    }

    private static Vector2 ResolveDragMetricsPosition(Vector2 referencePosition, Vector2 boxSize, float scale)
    {
        var io = ImGui.GetIO();
        var margin = 8f * scale;
        var rightPosition = referencePosition + new Vector2(18f * scale, 32f * scale);
        var leftPosition = referencePosition + new Vector2(-(boxSize.X + (18f * scale)), 32f * scale);

        var rightMin = rightPosition;
        var rightMax = rightPosition + boxSize;
        var mousePadding = 10f * scale;
        var overlapsMouse = io.MousePos.X >= rightMin.X - mousePadding
                            && io.MousePos.X <= rightMax.X + mousePadding
                            && io.MousePos.Y >= rightMin.Y - mousePadding
                            && io.MousePos.Y <= rightMax.Y + mousePadding;

        var position = overlapsMouse || rightMax.X > io.DisplaySize.X - margin
            ? leftPosition
            : rightPosition;
        position.X = Math.Clamp(position.X, margin, Math.Max(margin, io.DisplaySize.X - boxSize.X - margin));
        position.Y = Math.Clamp(position.Y, margin, Math.Max(margin, io.DisplaySize.Y - boxSize.Y - margin));
        return position;
    }
}

