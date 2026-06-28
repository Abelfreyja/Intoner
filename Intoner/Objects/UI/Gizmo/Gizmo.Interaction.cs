using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class Gizmo
{
    private static float ResolveCenterInteractionRadius(float scale)
        => GizmoConstants.CenterInteractionRadius * scale;

    private int BuildLinearGizmoAxisVisualStates(in GizmoContext context, bool useWorldSpace)
    {
        var axisCount = 0;
        for (var index = 0; index < GizmoAxisUtility.AxisCount; ++index)
        {
            var axis = GizmoAxisUtility.FromIndex(index);
            if (!TryBuildGizmoAxisVisual(context, axis, useWorldSpace, out var state))
            {
                continue;
            }

            AxisVisualStates[axisCount++] = state;
        }

        return axisCount;
    }

    private GizmoLinearInteractionState ResolveLinearInteractionState(
        in GizmoContext context,
        GizmoTransformMode mode,
        int axisCount,
        Vector2 mousePos,
        float scale)
    {
        TryGetGizmoInteractionBounds(axisCount, context.ScreenPos, scale, out var boundsMin, out var boundsMax);
        var availability = new GizmoInteractionAvailability(
            IsMouseWithinRect(boundsMin, boundsMax),
            IsGizmoDragActive(context.PrimarySnapshot.Id, mode),
            IsGizmoSurfaceDragActive(context.PrimarySnapshot.Id),
            IsGizmoWheelOpen());

        var hoveredAxis = GizmoAxis.None;
        var hoveredState = GizmoAxisVisualState.None;
        if (availability.CanResolveHover)
        {
            hoveredAxis = TryFindHoveredLinearAxis(axisCount, mousePos, scale, mode, out hoveredState)
                ? hoveredState.Axis
                : GizmoAxis.None;
        }

        var centerHovered = availability.CanResolveHover
                            && IsMouseWithinCircle(context.ScreenPos, ResolveCenterInteractionRadius(scale));
        if (centerHovered)
        {
            hoveredAxis = GizmoAxis.None;
            hoveredState = GizmoAxisVisualState.None;
        }

        var phase = availability.ResolvePhase(centerHovered, hoveredAxis != GizmoAxis.None);
        var common = new GizmoInteractionState(
            phase,
            availability.PointerInRegion,
            centerHovered,
            ResolveCurrentLinearActiveAxis(mode));

        return new GizmoLinearInteractionState(
            common,
            hoveredAxis,
            hoveredState,
            hoveredAxis != GizmoAxis.None);
    }

    private GizmoRotationInteractionState ResolveRotationInteractionState(
        in GizmoContext context,
        in RotationProjectionContext projection,
        Vector2 mousePos,
        float scale)
    {
        var padding = new Vector2(ResolveRotationInteractionRadius(projection, scale));
        var availability = new GizmoInteractionAvailability(
            IsMouseWithinRect(context.ScreenPos - padding, context.ScreenPos + padding),
            IsGizmoDragActive(context.PrimarySnapshot.Id, GizmoTransformMode.Rotation),
            IsGizmoSurfaceDragActive(context.PrimarySnapshot.Id),
            IsGizmoWheelOpen());

        var hoverState = RotationHoverState.None(float.MaxValue);
        if (availability.CanResolveHover)
        {
            TryFindRotationHoverState(projection, mousePos, scale, out hoverState);
        }

        var centerHovered = availability.CanResolveHover
                            && IsMouseWithinCircle(context.ScreenPos, ResolveCenterInteractionRadius(scale));
        if (centerHovered)
        {
            hoverState = RotationHoverState.None(float.MaxValue);
        }

        var phase = availability.ResolvePhase(centerHovered, hoverState.Axis != GizmoAxis.None);
        var common = new GizmoInteractionState(
            phase,
            availability.PointerInRegion,
            centerHovered,
            RotationDragState.ActiveAxis);

        return new GizmoRotationInteractionState(
            common,
            hoverState,
            hoverState.Axis != GizmoAxis.None);
    }

    private GizmoAxis ResolveCurrentLinearActiveAxis(GizmoTransformMode mode)
        => mode == GizmoTransformMode.Translation
            ? TranslationDragState.ActiveAxis
            : mode == GizmoTransformMode.Scale
                ? ScaleDragState.ActiveAxis
                : GizmoAxis.None;

    private bool TryFindHoveredLinearAxis(
        int axisCount,
        Vector2 mousePos,
        float scale,
        GizmoTransformMode mode,
        out GizmoAxisVisualState hoveredState)
    {
        hoveredState = GizmoAxisVisualState.None;
        var bestDistance = float.MaxValue;
        for (var index = 0; index < axisCount; ++index)
        {
            var state = AxisVisualStates[index];
            if (!TryGetLinearGizmoAxisHoverDistance(state, mousePos, scale, mode, out var distance)
                || distance >= bestDistance)
            {
                continue;
            }

            hoveredState = state;
            bestDistance = distance;
        }

        return hoveredState.Axis != GizmoAxis.None;
    }

    private bool TryFindRotationHoverState(
        in RotationProjectionContext projection,
        Vector2 mousePos,
        float scale,
        out RotationHoverState hoverState)
    {
        hoverState = RotationHoverState.None(GizmoConstants.RotationHoverTolerance * scale);

        for (var index = 0; index < GizmoAxisUtility.AxisCount; ++index)
        {
            TryUpdateRotationAxisHoverState(projection, GizmoAxisUtility.FromIndex(index), mousePos, ref hoverState);
        }

        return hoverState.Axis != GizmoAxis.None && hoverState.Distance <= GizmoConstants.RotationHoverTolerance * scale;
    }

    private static void TryUpdateRotationAxisHoverState(
        in RotationProjectionContext projection,
        GizmoAxis axis,
        Vector2 mousePos,
        ref RotationHoverState hoverState)
    {
        if (!TryResolveClosestRotationRingPoint(projection, axis, mousePos, out var distance, out var tangent, out var screenPoint, out var angle)
            || distance >= hoverState.Distance)
        {
            return;
        }

        hoverState = new RotationHoverState(axis, distance, tangent, screenPoint, true, angle);
    }

    private bool TryBuildGizmoAxisVisual(
        in GizmoContext context,
        GizmoAxis axis,
        bool useWorldSpace,
        out GizmoAxisVisualState state)
    {
        state = GizmoAxisVisualState.None;

        var targetLength = context.AxisWorldLength;
        var scale = ImGuiHelpers.GlobalScale;
        var maxScreenLength = GizmoConstants.AxisMaxScreenLength * scale;
        var baseScreenLength = ResolveObjectAwareScreenSize(GizmoConstants.AxisBaseScreenLength, context.AxisWorldLength, scale);
        var worldDirection = ResolveAxisWorldDirection(axis, context.Rotation, useWorldSpace);
        if (!ObjectMathUtility.HasLength(worldDirection))
        {
            return false;
        }

        Vector2? projectedScreenDirection = null;
        var projectedScreenLength = 0f;
        var axisWorldEnd = context.PivotPosition + (worldDirection * targetLength);
        if (ObjectViewportProjectionUtility.TryProjectWorldPointToViewport(
                context.ViewProjection,
                axisWorldEnd,
                context.ViewportPos,
                context.ViewportSize,
                out var projectedScreenEnd))
        {
            var projectedScreenVector = projectedScreenEnd - context.ScreenPos;
            projectedScreenLength = projectedScreenVector.Length();
            if (ObjectMathUtility.HasLength(projectedScreenLength))
            {
                projectedScreenDirection = projectedScreenVector / projectedScreenLength;
            }
        }

        var previousScreenDirection = TryGetCachedAxisScreenDirection(axis, out var storedScreenDirection)
            ? storedScreenDirection
            : (Vector2?)null;
        var fallbackScreenDirection = ResolveAxisFallbackScreenDirection(context, axis, projectedScreenDirection, previousScreenDirection);
        if (!ObjectMathUtility.HasLength(fallbackScreenDirection))
        {
            return false;
        }

        var drawScreenDirection = fallbackScreenDirection;
        var drawScreenLength = baseScreenLength;
        if (projectedScreenDirection.HasValue)
        {
            drawScreenDirection = projectedScreenDirection.Value;
            drawScreenLength = Math.Clamp(projectedScreenLength, baseScreenLength, maxScreenLength);

            var alignmentBlend = 0f;
            if (context.CameraViewDirection.HasValue)
            {
                var alignment = MathF.Abs(Vector3.Dot(worldDirection, context.CameraViewDirection.Value));
                if (alignment > GizmoConstants.AxisCameraShiftStartAlignment)
                {
                    alignmentBlend = Math.Clamp(
                        (alignment - GizmoConstants.AxisCameraShiftStartAlignment)
                        / (GizmoConstants.AxisCameraShiftEndAlignment - GizmoConstants.AxisCameraShiftStartAlignment),
                        0f,
                        1f);
                }
            }

            var lengthBlend = projectedScreenLength < baseScreenLength && ObjectMathUtility.HasLength(baseScreenLength)
                ? Math.Clamp(1f - (projectedScreenLength / baseScreenLength), 0f, 1f)
                : 0f;
            var blend = MathF.Max(alignmentBlend, lengthBlend);
            if (blend > 0f)
            {
                var blendedDirection = Vector2.Lerp(drawScreenDirection, fallbackScreenDirection, blend);
                if (ObjectMathUtility.TryNormalize(blendedDirection, out var normalizedDirection))
                {
                    drawScreenDirection = normalizedDirection;
                }
            }
        }

        var visualScale = ResolveCompensatedLinearVisualScale(projectedScreenLength, drawScreenLength, scale);
        var axisScreenEnd = context.ScreenPos + (drawScreenDirection * drawScreenLength);
        state = new GizmoAxisVisualState(
            axis,
            context.ScreenPos,
            axisScreenEnd,
            worldDirection,
            targetLength,
            drawScreenDirection,
            drawScreenLength,
            visualScale);
        CacheAxisScreenDirection(axis, drawScreenDirection);
        return true;
    }

    private static float ResolveCompensatedLinearVisualScale(float projectedScreenLength, float drawScreenLength, float scale)
    {
        if (!ObjectMathUtility.HasLength(projectedScreenLength) || projectedScreenLength >= drawScreenLength)
        {
            return scale;
        }

        var ratio = drawScreenLength / MathF.Max(projectedScreenLength, 1f * scale);
        return scale * Math.Clamp(ratio, 1f, GizmoConstants.AxisMaxCompensatedHandleScale);
    }

    private static bool TryGetLinearGizmoAxisHoverDistance(
        in GizmoAxisVisualState state,
        Vector2 mousePos,
        float scale,
        GizmoTransformMode mode,
        out float distance)
    {
        distance = float.MaxValue;
        var interactionStart = GetLinearGizmoInteractionStart(state, scale);
        var interactionEnd = GetLinearGizmoInteractionEnd(state, mode, state.VisualScale);
        var lineHitRadius = ResolveLinearGizmoAxisLineHitRadius(mode, state.VisualScale);

        if (TryGetSegmentHitDistance(mousePos, interactionStart, interactionEnd, lineHitRadius, out var lineDistance))
        {
            distance = lineDistance;
        }

        if (mode == GizmoTransformMode.Scale)
        {
            if (TryGetScaleHandleHitDistance(mousePos, state.ScreenEnd, state.VisualScale, out var handleDistance))
            {
                distance = MathF.Min(distance, handleDistance);
            }
        }
        else if (TryGetTranslationArrowHitDistance(mousePos, state, state.VisualScale, out var arrowDistance))
        {
            distance = MathF.Min(distance, arrowDistance);
        }

        return distance < float.MaxValue;
    }

    private bool TryGetCachedAxisScreenDirection(GizmoAxis axis, out Vector2 screenDirection)
    {
        var index = GizmoAxisUtility.ToIndex(axis);
        if (index < 0)
        {
            screenDirection = default;
            return false;
        }

        var cachedScreenDirection = State.PreviousAxisScreenDirections[index];
        if (!cachedScreenDirection.HasValue
            || !ObjectMathUtility.TryNormalize(cachedScreenDirection.Value, out screenDirection))
        {
            screenDirection = default;
            return false;
        }

        return true;
    }

    private void CacheAxisScreenDirection(GizmoAxis axis, Vector2 screenDirection)
    {
        var index = GizmoAxisUtility.ToIndex(axis);
        if (index < 0 || !ObjectMathUtility.TryNormalize(screenDirection, out var normalizedScreenDirection))
        {
            return;
        }

        State.PreviousAxisScreenDirections[index] = normalizedScreenDirection;
    }

    private static bool TryResolveClosestRotationRingPoint(
        in RotationProjectionContext projection,
        GizmoAxis axis,
        Vector2 mousePos,
        out float distance,
        out Vector2 tangent,
        out Vector2 screenPoint,
        out float angle)
    {
        distance = float.MaxValue;
        tangent = Vector2.UnitX;
        screenPoint = default;
        angle = 0f;

        var axisDirection = ResolveAxisWorldDirection(axis, projection.Rotation, projection.UseWorldSpace);
        var rotationMathProjection = CreateRotationMathProjection(projection);
        Vector2? previousPos = null;
        var previousVisible = false;
        var hasPrevious = false;

        for (var index = 0; index <= GizmoConstants.RotationRingSegments; ++index)
        {
            var currentAngle = (index / (float)GizmoConstants.RotationRingSegments) * (MathF.PI * 2f);
            if (!GizmoRotationMath.TryProjectAxisPoint(rotationMathProjection, axisDirection, currentAngle, out var currentPos, out var isVisible))
            {
                hasPrevious = false;
                previousPos = null;
                previousVisible = false;
                continue;
            }

            if (hasPrevious && previousPos.HasValue && previousVisible && isVisible)
            {
                var fromPos = previousPos.Value;
                var segment = currentPos - fromPos;
                var segmentLengthSq = segment.LengthSquared();
                if (ObjectMathUtility.HasLength(segmentLengthSq))
                {
                    var t = Vector2.Dot(mousePos - fromPos, segment) / segmentLengthSq;
                    t = Math.Clamp(t, 0f, 1f);
                    var projectedPoint = fromPos + (segment * t);
                    var projectedDistance = (mousePos - projectedPoint).Length();
                    if (projectedDistance < distance)
                    {
                        var previousAngle = ((index - 1) / (float)GizmoConstants.RotationRingSegments) * (MathF.PI * 2f);
                        distance = projectedDistance;
                        tangent = ObjectMathUtility.TryNormalize(segment, out var normalizedTangent)
                            ? normalizedTangent
                            : Vector2.UnitX;
                        screenPoint = projectedPoint;
                        angle = GizmoRotationMath.NormalizeAngle(previousAngle + ((currentAngle - previousAngle) * t));
                    }
                }
            }

            hasPrevious = true;
            previousPos = currentPos;
            previousVisible = isVisible;
        }

        return distance < float.MaxValue;
    }

    private static Vector2 ResolveAxisFallbackScreenDirection(
        in GizmoContext context,
        GizmoAxis axis,
        Vector2? projectedScreenDirection,
        Vector2? previousScreenDirection)
    {
        if (previousScreenDirection.HasValue && ObjectMathUtility.TryNormalize(previousScreenDirection.Value, out var normalizedPreviousDirection))
        {
            return normalizedPreviousDirection;
        }

        var fallbackDirection = axis switch
        {
            GizmoAxis.X => TryProjectCameraPlaneScreenDirection(context, context.CameraRight),
            GizmoAxis.Y => TryProjectCameraPlaneScreenDirection(context, context.CameraUp),
            GizmoAxis.Z => TryProjectCameraPlaneScreenDirection(
                context,
                context.CameraRight.HasValue
                && context.CameraUp.HasValue
                && ObjectMathUtility.TryNormalize(context.CameraRight.Value - context.CameraUp.Value, out var diagonalDirection)
                    ? diagonalDirection
                    : null),
            _ => null,
        };

        if (fallbackDirection.HasValue && ObjectMathUtility.TryNormalize(fallbackDirection.Value, out var normalizedFallback))
        {
            if (projectedScreenDirection.HasValue && Vector2.Dot(normalizedFallback, projectedScreenDirection.Value) < 0f)
            {
                normalizedFallback = -normalizedFallback;
            }

            return normalizedFallback;
        }

        return axis switch
        {
            GizmoAxis.X => Vector2.UnitX,
            GizmoAxis.Y => -Vector2.UnitY,
            GizmoAxis.Z => ResolveFallbackZScreenDirection(),
            _ => Vector2.Zero,
        };
    }

    private static Vector2? TryProjectCameraPlaneScreenDirection(in GizmoContext context, Vector3? worldDirection)
    {
        if (!worldDirection.HasValue || !ObjectMathUtility.HasLength(worldDirection.Value))
        {
            return null;
        }

        var projectedPoint = context.PivotPosition + (worldDirection.Value * context.AxisWorldLength);
        if (!ObjectViewportProjectionUtility.TryProjectWorldPointToViewport(
                context.ViewProjection,
                projectedPoint,
                context.ViewportPos,
                context.ViewportSize,
                out var projectedScreenPoint))
        {
            return null;
        }

        var screenVector = projectedScreenPoint - context.ScreenPos;
        return !ObjectMathUtility.TryNormalize(screenVector, out var normalizedScreenVector)
            ? null
            : normalizedScreenVector;
    }

    private void TryGetGizmoInteractionBounds(int axisCount, Vector2 screenPos, float scale, out Vector2 min, out Vector2 max)
    {
        min = screenPos;
        max = screenPos;

        if (axisCount <= 0)
        {
            return;
        }

        min = new Vector2(float.MaxValue, float.MaxValue);
        max = new Vector2(float.MinValue, float.MinValue);
        var paddingScale = scale;
        for (var index = 0; index < axisCount; ++index)
        {
            var state = AxisVisualStates[index];
            min = Vector2.Min(min, Vector2.Min(state.ScreenStart, state.ScreenEnd));
            max = Vector2.Max(max, Vector2.Max(state.ScreenStart, state.ScreenEnd));
            paddingScale = MathF.Max(paddingScale, state.VisualScale);
        }

        min = Vector2.Min(min, screenPos) - new Vector2(35f * paddingScale);
        max = Vector2.Max(max, screenPos) + new Vector2(35f * paddingScale);
    }

    private static Vector3 ResolveAxisWorldDirection(GizmoAxis axis, Quaternion rotation, bool useWorldSpace)
    {
        var axisDirection = GizmoAxisUtility.ToUnitVector(axis);
        if (!ObjectMathUtility.HasLength(axisDirection) || useWorldSpace)
        {
            return axisDirection;
        }

        var rotated = Vector3.Transform(axisDirection, rotation);
        return !ObjectMathUtility.TryNormalize(rotated, out var normalizedRotated)
            ? axisDirection
            : normalizedRotated;
    }

    private static Vector2 GetLinearGizmoInteractionStart(GizmoAxisVisualState state, float scale)
    {
        if (!ObjectMathUtility.TryNormalize(state.ScreenDirection, out var screenDirection))
        {
            return state.ScreenStart;
        }

        var inset = MathF.Min(
            ResolveCenterInteractionRadius(scale) + (5f * scale),
            MathF.Max(0f, state.ScreenLength - 1f));
        return inset <= 0f
            ? state.ScreenStart
            : state.ScreenStart + (screenDirection * inset);
    }

    private static Vector2 GetLinearGizmoInteractionEnd(GizmoAxisVisualState state, GizmoTransformMode mode, float scale)
    {
        if (!ObjectMathUtility.TryNormalize(state.ScreenDirection, out var screenDirection))
        {
            return state.ScreenEnd;
        }

        if (mode == GizmoTransformMode.Translation)
        {
            return GetTrimmedGizmoEndpoint(state, scale);
        }

        var trimAmount = GetScaleHandleVisualHalfExtent(scale) + (1.5f * scale);
        trimAmount = MathF.Min(trimAmount, MathF.Max(0f, state.ScreenLength - 1f));
        return trimAmount <= 0f
            ? state.ScreenEnd
            : state.ScreenEnd - (screenDirection * trimAmount);
    }

    private static float ResolveLinearGizmoAxisLineHitRadius(GizmoTransformMode mode, float scale)
        => mode == GizmoTransformMode.Scale
            ? MathF.Max(7f * scale, GizmoConstants.AxisLineThickness * scale * 2.35f)
            : MathF.Max(6f * scale, GizmoConstants.AxisLineThickness * scale * 2.1f);

    private static bool TryGetSegmentHitDistance(
        Vector2 point,
        Vector2 start,
        Vector2 end,
        float hitRadius,
        out float distance)
    {
        distance = float.MaxValue;
        if (hitRadius <= 0f)
        {
            return false;
        }

        distance = DistanceToSegment(point, start, end);
        return distance <= hitRadius;
    }

    private static bool TryGetTranslationArrowHitDistance(Vector2 point, in GizmoAxisVisualState state, float scale, out float distance)
    {
        distance = float.MaxValue;
        if (!ObjectMathUtility.TryNormalize(state.ScreenDirection, out var direction))
        {
            return false;
        }

        var normal = new Vector2(-direction.Y, direction.X);
        var arrowLength = GizmoConstants.AxisArrowLength * scale;
        var arrowWidth = GizmoConstants.AxisArrowWidth * scale;
        var tip = state.ScreenEnd;
        var basePoint = tip - (direction * arrowLength);
        var left = basePoint + (normal * arrowWidth);
        var right = basePoint - (normal * arrowWidth);
        var edgePadding = MathF.Max(3.5f * scale, 2f);

        if (IsPointWithinTriangle(point, tip, left, right))
        {
            distance = 0f;
            return true;
        }

        var tipDistance = DistanceToSegment(point, tip, left);
        var leftDistance = DistanceToSegment(point, left, right);
        var rightDistance = DistanceToSegment(point, right, tip);
        distance = MathF.Min(tipDistance, MathF.Min(leftDistance, rightDistance));
        return distance <= edgePadding;
    }

    private static bool TryGetScaleHandleHitDistance(
        Vector2 point,
        Vector2 center,
        float scale,
        out float distance)
    {
        var halfExtent = GetScaleHandleVisualHalfExtent(scale) + MathF.Max(3.5f * scale, 2f);
        var min = center - new Vector2(halfExtent);
        var max = center + new Vector2(halfExtent);
        var clamped = Vector2.Clamp(point, min, max);
        distance = Vector2.Distance(point, clamped);
        return ObjectMathUtility.IsNearlyZero(distance);
    }

    private static float GetScaleHandleVisualHalfExtent(float scale)
        => GizmoConstants.ScaleHandleSize * scale * 0.5f;

    private static bool IsPointWithinTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
    {
        var denominator = ((b.Y - c.Y) * (a.X - c.X)) + ((c.X - b.X) * (a.Y - c.Y));
        if (ObjectMathUtility.IsNearlyZero(denominator))
        {
            return false;
        }

        var alpha = (((b.Y - c.Y) * (point.X - c.X)) + ((c.X - b.X) * (point.Y - c.Y))) / denominator;
        var beta = (((c.Y - a.Y) * (point.X - c.X)) + ((a.X - c.X) * (point.Y - c.Y))) / denominator;
        var gamma = 1f - alpha - beta;
        return alpha >= 0f && beta >= 0f && gamma >= 0f;
    }

    private static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        var segment = end - start;
        var lengthSq = segment.LengthSquared();
        if (!ObjectMathUtility.HasLength(lengthSq))
        {
            return (point - start).Length();
        }

        var t = Vector2.Dot(point - start, segment) / lengthSq;
        t = Math.Clamp(t, 0f, 1f);
        var projection = start + (segment * t);
        return (point - projection).Length();
    }

    private static bool IsMouseWithinRect(Vector2 min, Vector2 max)
    {
        var mouse = ImGui.GetIO().MousePos;
        return mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y;
    }

    private static bool IsMouseWithinCircle(Vector2 center, float radius)
        => Vector2.DistanceSquared(ImGui.GetIO().MousePos, center) <= (radius * radius);

    private static Vector2 GetTrimmedGizmoEndpoint(GizmoAxisVisualState state, float scale)
    {
        if (!ObjectMathUtility.TryNormalize(state.ScreenDirection, out var screenDirection))
        {
            return state.ScreenEnd;
        }

        var trimAmount = MathF.Min(GizmoConstants.AxisArrowLength * scale, MathF.Max(0f, state.ScreenLength - 1f));
        return trimAmount <= 0f
            ? state.ScreenEnd
            : state.ScreenEnd - (screenDirection * trimAmount);
    }

    private static Vector2 ResolveFallbackZScreenDirection()
        => ObjectMathUtility.TryNormalize(new Vector2(0.85f, -0.65f), out var direction)
            ? direction
            : Vector2.UnitX;
}

