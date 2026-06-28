using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Intoner.Objects.Models;
using Intoner.Objects.Rendering;
using Intoner.Objects.Rendering.Drawing;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class Gizmo
{
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

    private static Vector4 GetAxisColorVector(GizmoAxis axis, bool isActive, bool isHovered)
    {
        var index = GizmoAxisUtility.ToIndex(axis);
        if (index < 0)
        {
            return EditorColors.Text;
        }

        var baseColor = EditorColors.GizmoAxisBase(axis);
        var intensity = isActive ? 1.35f : isHovered ? 1.15f : 0.95f;
        return EditorColors.Color(
            MathF.Min(baseColor.X * intensity, 1f),
            MathF.Min(baseColor.Y * intensity, 1f),
            MathF.Min(baseColor.Z * intensity, 1f),
            0.95f);
    }

    private static Vector4 GetAxisGlowColorVector(GizmoAxis axis, bool isActive)
    {
        var index = GizmoAxisUtility.ToIndex(axis);
        if (index < 0)
        {
            return EditorColors.Color(0f, 0f, 0f, 0.2f);
        }

        var baseColor = EditorColors.GizmoAxisBase(axis);
        return EditorColors.Color(baseColor.X, baseColor.Y, baseColor.Z, isActive ? 0.45f : 0.25f);
    }

    private static Vector4 GetAxisBackgroundColorVector(GizmoAxis axis, bool isActive)
    {
        var index = GizmoAxisUtility.ToIndex(axis);
        if (index < 0)
        {
            return EditorColors.TextDisabled;
        }

        var baseColor = EditorColors.GizmoAxisBase(axis);
        return EditorColors.Color(baseColor.X, baseColor.Y, baseColor.Z, isActive ? 0.45f : 0.20f);
    }

    private static float ResolveGizmoAlpha(bool isFocused)
        => isFocused ? GizmoConstants.ActiveAlpha : GizmoConstants.IdleAlpha;

    private static float ClampGizmoAlpha(float value)
        => Math.Clamp(value, 0f, 1f);

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

        drawList.AddRectFilled(rectMin, rectMax, ImGui.GetColorU32(EditorColors.Color(0f, 0f, 0f, 0.55f)), 4f * scale);
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

    private static void DrawSuppressedGizmoAxis(DrawBatch batch, GizmoAxisVisualState state)
    {
        batch.AddScreenLine(
            state.ScreenStart,
            state.ScreenEnd,
            EditorColors.GizmoTranslationDragSuppressed,
            GizmoConstants.AxisLineThickness * state.VisualScale * 0.75f);
    }

    private static void DrawAxisArrowhead(DrawBatch batch, GizmoAxisVisualState state, float scale, Vector4 axisColor)
    {
        if (!ObjectMathUtility.TryNormalize(state.ScreenDirection, out var direction))
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
        var outlineColor = EditorColors.Color(axisColor.X, axisColor.Y, axisColor.Z, MathF.Min(axisColor.W + 0.15f, 1f));
        batch.AddScreenLine(tip, left, outlineColor, 1.1f * scale);
        batch.AddScreenLine(left, right, outlineColor, 1.1f * scale);
        batch.AddScreenLine(right, tip, outlineColor, 1.1f * scale);
    }

    private static void DrawScaleHandle(DrawBatch batch, Vector2 position, float scale, Vector4 axisColor, bool isActive, bool isHovered)
    {
        var half = new Vector2(GizmoConstants.ScaleHandleSize * scale * 0.5f);
        var highlight = isActive || isHovered;
        var fillColor = EditorColors.Color(axisColor.X, axisColor.Y, axisColor.Z, highlight ? 0.95f : 0.65f);
        batch.AddScreenRectFilled(position - half, position + half, fillColor);
        batch.AddScreenRect(position - half, position + half, EditorColors.Color(axisColor.X, axisColor.Y, axisColor.Z, 0.95f), 1f * scale);
    }

    private static void DrawAxisLabel(ImDrawListPtr drawList, GizmoAxisVisualState state, float scale, Vector4 axisColor, string label, bool isActive, bool isHovered)
    {
        if (string.IsNullOrEmpty(label))
        {
            return;
        }

        var direction = !ObjectMathUtility.TryNormalize(state.ScreenDirection, out var normalizedDirection)
            ? Vector2.UnitX
            : normalizedDirection;
        var center = state.ScreenEnd + (direction * GizmoConstants.AxisLabelDistance * scale);
        var textSize = ImGui.CalcTextSize(label);
        var padding = new Vector2(GizmoConstants.AxisLabelPadding * scale);
        var rectMin = center - (textSize * 0.5f) - padding;
        var rectMax = center + (textSize * 0.5f) + padding;
        var backgroundColor = EditorColors.Color(axisColor.X, axisColor.Y, axisColor.Z, isActive || isHovered ? 0.95f : 0.70f);
        var textColor = isActive || isHovered
            ? EditorColors.Color(0f, 0f, 0f, 0.95f)
            : EditorColors.Color(0.05f, 0.05f, 0.05f, 0.90f);

        drawList.AddRectFilled(rectMin, rectMax, ImGui.GetColorU32(backgroundColor), GizmoConstants.AxisLabelRoundness * scale);
        drawList.AddRect(rectMin, rectMax, ImGui.GetColorU32(EditorColors.Color(axisColor.X, axisColor.Y, axisColor.Z, 0.95f)), GizmoConstants.AxisLabelRoundness * scale, ImDrawFlags.None, 1.05f * scale);
        drawList.AddText(center - (textSize * 0.5f), ImGui.GetColorU32(textColor), label);
    }

    private static void DrawCircularCenterHandle(DrawBatch batch, Vector2 screenPos, float scale, bool isHovered, bool isActive, bool alignToSurfaceNormal)
    {
        var radius = GizmoConstants.CenterPointRadius * scale;
        var fillColor = isActive
            ? EditorColors.Color(1f, 1f, 1f, 1f)
            : isHovered
                ? EditorColors.Color(1f, 1f, 1f, 0.98f)
                : EditorColors.Color(1f, 1f, 1f, 0.95f);
        batch.AddScreenCircleFilled(screenPos, radius, fillColor, 32);

        if (!isHovered && !isActive)
        {
            return;
        }

        var accentColor = alignToSurfaceNormal
            ? EditorColors.AccentGreen
            : EditorColors.AccentBlue;
        accentColor.W = isActive ? 0.95f : 0.80f;
        batch.AddScreenCircle(
            screenPos,
            ResolveCenterInteractionRadius(scale),
            accentColor,
            MathF.Max(1.4f * scale, 1f),
            48);
    }

    private void DrawGizmoLabel(ImDrawListPtr drawList, in GizmoContext context, float scale)
    {
        var modeLabel = Mode switch
        {
            GizmoTransformMode.Translation => "Move",
            GizmoTransformMode.Rotation => "Rotate",
            GizmoTransformMode.Scale => "Scale",
            _ => string.Empty,
        };

        var labelRoot = context.SelectionCount == 1
            ? context.PrimarySnapshot.Name
            : $"{context.SelectionCount} objects";
        var label = string.IsNullOrEmpty(modeLabel)
            ? labelRoot
            : $"{labelRoot} [{modeLabel}]";
        var textSize = ImGui.CalcTextSize(label);
        var textPos = context.ScreenPos + new Vector2((GizmoConstants.CenterPointRadius * scale) + (6f * scale), -(textSize.Y * 0.5f));
        drawList.AddText(textPos, ImGui.GetColorU32(ImGuiCol.Text), label);
    }

    private static float ResolveRotationInteractionRadius(in RotationProjectionContext projection, float scale)
        => projection.VisualRadius + (GizmoConstants.RotationInteractionPadding * scale);

    private void DrawRotationAxes(
        DrawBatch batch,
        in RotationProjectionContext projection,
        float scale,
        in GizmoInteractionState common,
        in RotationHoverState hoverState,
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
                projection,
                GizmoConstants.RotationRingThickness * scale,
                axis,
                isActive: common.DragActive && axis == common.ActiveAxis,
                isHovered: common.Phase == GizmoInteractionPhase.HoverAxis && axis == hoverState.Axis,
                pass);
        }
    }

    private void DrawRotationAxisPath(
        DrawBatch batch,
        in RotationProjectionContext projection,
        float thickness,
        GizmoAxis axis,
        bool isActive,
        bool isHovered,
        RotationAxisSegmentPass pass)
    {
        var axisDirection = ResolveAxisWorldDirection(axis, projection.Rotation, projection.UseWorldSpace);
        var rotationMathProjection = CreateRotationMathProjection(projection);
        Span<Vector2> projectedPoints = stackalloc Vector2[GizmoConstants.RotationRingSegments + 1];
        Span<bool> validPoints = stackalloc bool[GizmoConstants.RotationRingSegments + 1];
        Span<bool> visiblePoints = stackalloc bool[GizmoConstants.RotationRingSegments + 1];

        for (var index = 0; index <= GizmoConstants.RotationRingSegments; ++index)
        {
            var angle = index / (float)GizmoConstants.RotationRingSegments * MathF.Tau;
            if (!GizmoRotationMath.TryProjectAxisPoint(rotationMathProjection, axisDirection, angle, out var currentPos, out var isVisible))
            {
                continue;
            }

            projectedPoints[index] = currentPos;
            validPoints[index] = true;
            visiblePoints[index] = isVisible;
        }

        for (var segment = 1; segment <= GizmoConstants.RotationRingSegments; ++segment)
        {
            if (!ShouldDrawRotationSegment(validPoints, visiblePoints, segment, pass, out var segmentVisible))
            {
                continue;
            }

            var caps = ResolveRotationSegmentCaps(validPoints, visiblePoints, segment, pass);
            var color = segmentVisible
                ? GetAxisColorVector(axis, isActive, isHovered)
                : GetAxisBackgroundColorVector(axis, isActive);
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
        RotationAxisSegmentPass pass,
        out bool segmentVisible)
    {
        segmentVisible = false;
        if (!validPoints[segment - 1] || !validPoints[segment])
        {
            return false;
        }

        segmentVisible = visiblePoints[segment - 1] && visiblePoints[segment];
        return (pass == RotationAxisSegmentPass.Visible) == segmentVisible;
    }

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

        if (ShouldDrawRotationSegment(validPoints, visiblePoints, previousSegment, pass, out _))
        {
            caps &= ~ScreenLineCaps.Start;
        }

        if (ShouldDrawRotationSegment(validPoints, visiblePoints, nextSegment, pass, out _))
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
        if (ObjectMathUtility.IsNearlyZero(deltaRadians, 0.0001f))
        {
            return;
        }

        var steps = Math.Clamp((int)(MathF.Abs(deltaRadians) / (MathF.PI * 2f) * GizmoConstants.RotationRingSegments), 4, GizmoConstants.RotationRingSegments);
        var axisDirection = ResolveAxisWorldDirection(RotationDragState.ActiveAxis, projection.Rotation, projection.UseWorldSpace);
        var rotationMathProjection = CreateRotationMathProjection(projection);
        var highlightColor = EditorColors.GizmoRotationDragHighlight;
        var fillColor = EditorColors.WithAlpha(highlightColor, GizmoConstants.RotationHighlightSectorFillAlpha);
        var boundaryColor = EditorColors.WithAlpha(highlightColor, GizmoConstants.RotationHighlightSectorBoundaryAlpha);
        var arcThickness = GizmoConstants.RotationRingThickness * scale * GizmoConstants.RotationHighlightThicknessMultiplier;
        var boundaryThickness = GizmoConstants.AxisLineThickness * scale * GizmoConstants.RotationHighlightSectorBoundaryThicknessMultiplier;
        Span<Vector2> projectedPoints = stackalloc Vector2[steps + 1];
        Span<bool> validPoints = stackalloc bool[steps + 1];
        for (var index = 0; index <= steps; ++index)
        {
            var t = index / (float)steps;
            var angle = RotationDragState.RotationDragStartAngle.Value + (deltaRadians * t);
            if (!GizmoRotationMath.TryProjectAxisPoint(rotationMathProjection, axisDirection, GizmoRotationMath.NormalizeAngle(angle), out var projected, out _))
            {
                continue;
            }

            projectedPoints[index] = projected;
            validPoints[index] = true;
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

    private void DrawRotationSnapTicks(
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
        var visibleColor = EditorColors.Color(0f, 0f, 0f, GizmoConstants.RotationSnapTickAlpha);
        var hiddenColor = EditorColors.Color(0f, 0f, 0f, GizmoConstants.RotationSnapTickHiddenAlpha);
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
            if (!ObjectMathUtility.TryNormalize(radial, out var radialDirection))
            {
                continue;
            }

            var normalizedAngle = GizmoRotationMath.NormalizeAngle(angle - angleOffset);
            var majorStepIndex = normalizedAngle / majorStepRadians;
            var isMajorTick = ObjectMathUtility.IsNearlyEqual(majorStepIndex, MathF.Round(majorStepIndex), 0.001f);
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
        drawList.AddText(UiBuilder.IconFont, fontSize, position, ImGui.GetColorU32(EditorColors.Color(1f, 1f, 1f, 0.55f)), icon);
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

