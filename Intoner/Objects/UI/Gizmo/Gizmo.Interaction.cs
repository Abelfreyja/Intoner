using Dalamud.Interface.Utility;
using Dalamud.Bindings.ImGui;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;

using Intoner.Scene;

namespace Intoner.Objects.UI;

internal sealed partial class Gizmo
{
    private GizmoFrame BuildGizmoFrame(in GizmoContext context, Vector2 mousePos, float scale, bool pointerAvailable)
    {
        GizmoTransformMode modes = context.CanTransform ? Settings.GetAvailableModes(context.ScaleSupported) : GizmoTransformMode.None;
        bool combined = modes.IsCombined();
        int translationAxisCount = (modes & GizmoTransformMode.Translation) != GizmoTransformMode.None
            ? BuildLinearGizmoAxisVisualStates(context, context.UseWorldSpace, State.TranslationAxes)
            : 0;
        int scaleAxisCount = 0;
        if ((modes & GizmoTransformMode.Scale) != GizmoTransformMode.None)
        {
            if (translationAxisCount > 0 && !context.UseWorldSpace)
            {
                scaleAxisCount = translationAxisCount;
                State.TranslationAxes.AsSpan(0, translationAxisCount).CopyTo(State.ScaleAxes);
            }
            else
            {
                scaleAxisCount = BuildLinearGizmoAxisVisualStates(context, false, State.ScaleAxes);
            }

            if (ScaleDragState.IsDragging)
            {
                for (int index = 0; index < scaleAxisCount; ++index)
                {
                    GizmoAxisVisualState axis = State.ScaleAxes[index];

                    if (axis.Matches(ScaleDragState.ActiveAxis, -ScaleDragState.AxisWorldDirection))
                    {
                        State.ScaleAxes[index] = axis.Opposite;
                    }
                }
            }

            if (Scaling.Anchored)
            {
                int count = scaleAxisCount;
                for (int index = 0; index < count; ++index)
                {
                    GizmoAxisVisualState axis = State.ScaleAxes[index];
                    State.ScaleAxes[scaleAxisCount++] = axis.Opposite;
                }
            }

            if (combined)
            {
                for (int index = 0; index < scaleAxisCount; ++index)
                {
                    GizmoAxisVisualState axis = State.ScaleAxes[index];
                    float length = axis.ScreenLength * GizmoConstants.CombinedScaleReach;
                    State.ScaleAxes[index] = axis with
                    {
                        ScreenLength = length,
                        ScreenEnd = axis.ScreenStart + axis.ScreenDirection * length,
                    };
                }
            }
        }
        RotationProjectionContext projection = default;
        if ((modes & GizmoTransformMode.Rotation) != GizmoTransformMode.None)
        {
            float radius = ResolveWorldScaledScreenSize(GizmoConstants.RotationRingBaseRadius, context.AxisWorldLength, scale);
            if (combined)
            {
                radius *= GizmoConstants.CombinedRotationRadius;
            }

            projection = RotationDragState.IsDragging && RotationDragState.RotationProjection.HasValue
                ? RotationDragState.RotationProjection.Value
                : CreateRotationProjectionContext(context, radius);
        }

        return ResolveGizmoInteraction(context, modes, translationAxisCount, scaleAxisCount, projection, mousePos, scale, pointerAvailable);
    }

    private int BuildLinearGizmoAxisVisualStates(in GizmoContext context, bool useWorldSpace, Span<GizmoAxisVisualState> axes)
    {
        int count = 0;
        for (int index = 0; index < GizmoAxisUtility.AxisCount; ++index)
        {
            if (!TryBuildGizmoAxisVisual(context, GizmoAxisUtility.FromIndex(index), useWorldSpace, out GizmoAxisVisualState axis))
            {
                continue;
            }

            axes[count++] = axis;
        }

        return count;
    }

    private GizmoFrame ResolveGizmoInteraction(
        in GizmoContext context,
        GizmoTransformMode modes,
        int translationAxisCount,
        int scaleAxisCount,
        in RotationProjectionContext projection,
        Vector2 mousePos,
        float scale,
        bool pointerAvailable)
    {
        ReadOnlySpan<GizmoAxisVisualState> translationAxes = State.TranslationAxes.AsSpan(0, translationAxisCount);
        ReadOnlySpan<GizmoAxisVisualState> scaleAxes = State.ScaleAxes.AsSpan(0, scaleAxisCount);
        GetGizmoInteractionBounds(translationAxes, context.ScreenPos, scale, out Vector2 min, out Vector2 max);
        GetGizmoInteractionBounds(scaleAxes, context.ScreenPos, scale, out Vector2 scaleMin, out Vector2 scaleMax);
        min = Vector2.Min(min, scaleMin);
        max = Vector2.Max(max, scaleMax);
        if (SceneGroupUtility.ResolveSelectedGroup(context.SelectedSnapshots) is not null)
        {
            Vector2 padding = new(ResolvePivotRingRadius(scale)
                + (GizmoConstants.PivotRingThickness * 0.5f + GizmoConstants.PivotRingHitPadding) * scale);
            min = Vector2.Min(min, context.ScreenPos - padding);
            max = Vector2.Max(max, context.ScreenPos + padding);
        }
        if ((modes & GizmoTransformMode.Rotation) != GizmoTransformMode.None)
        {
            Vector2 padding = new(ResolveRotationInteractionRadius(projection, scale));
            min = Vector2.Min(min, context.ScreenPos - padding);
            max = Vector2.Max(max, context.ScreenPos + padding);
        }

        bool pointerInRegion = pointerAvailable && mousePos.X >= min.X && mousePos.Y >= min.Y
            && mousePos.X <= max.X && mousePos.Y <= max.Y;
        bool dragging = TryGetActiveTransformDragState(out GizmoTransformDragSession? drag);
        GizmoInteractionAvailability availability = new(pointerInRegion, dragging,
            IsGizmoSurfaceDragActive(context.PrimarySnapshot.Id), IsGizmoWheelOpen() || ImGui.IsPopupOpen(PivotMenuId));
        bool pivotHovered = availability.CanResolveHover && IsPivotRingHovered(context, mousePos, scale);
        float centerRadius = GizmoLinearHandles.ResolveCenterInteractionRadius(scale);
        bool centerHovered = availability.CanResolveHover && context.CanTransform && context.SurfaceDragSupported
            && Vector2.DistanceSquared(mousePos, context.ScreenPos) <= centerRadius * centerRadius;
        GizmoHandleHit hit = default;
        if (availability.CanResolveHover && !centerHovered && !pivotHovered)
        {
            GizmoLinearHandles.FindLinearHandle(translationAxes, GizmoTransformMode.Translation, modes, [], mousePos, scale, ref hit);
            if (!hit.IsValid)
            {
                GizmoLinearHandles.FindLinearHandle(scaleAxes, GizmoTransformMode.Scale, modes, translationAxes, mousePos, scale, ref hit);
            }
            if ((modes & GizmoTransformMode.Rotation) != GizmoTransformMode.None)
            {
                RotationHoverState rotation = FindRotationHoverState(projection, mousePos, scale);
                GizmoHandleHit candidate = new(GizmoTransformMode.Rotation, default, rotation, rotation.Distance, false);
                if (rotation.IsValid && candidate.IsCloserThan(hit))
                {
                    hit = candidate;
                }
            }
        }

        GizmoInteractionState interaction = new(pivotHovered ? GizmoInteractionPhase.HoverPivot : availability.ResolvePhase(centerHovered, hit.IsValid),
            pointerInRegion, centerHovered, drag?.ActiveAxis ?? GizmoAxis.None);
        return new(context, modes, translationAxisCount, scaleAxisCount, projection, interaction, hit,
            drag?.Mode ?? GizmoTransformMode.None);
    }

    private static RotationHoverState FindRotationHoverState(
        in RotationProjectionContext projection,
        Vector2 mousePos,
        float scale)
    {
        var hoverState = RotationHoverState.None(GizmoConstants.RotationHoverTolerance * scale);

        for (var index = 0; index < GizmoAxisUtility.AxisCount; ++index)
        {
            TryUpdateRotationAxisHoverState(projection, GizmoAxisUtility.FromIndex(index), mousePos, ref hoverState);
        }

        return hoverState;
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
        var baseScreenLength = ResolveWorldScaledScreenSize(GizmoConstants.AxisBaseScreenLength, context.AxisWorldLength, scale);
        var worldDirection = ResolveAxisWorldDirection(axis, context.Rotation, useWorldSpace);
        if (!NumericsUtility.HasLength(worldDirection))
        {
            return false;
        }

        ref GizmoAxisProjectionState cached = ref (useWorldSpace
            ? ref State.WorldAxisProjections[GizmoAxisUtility.ToIndex(axis)]
            : ref State.LocalAxisProjections[GizmoAxisUtility.ToIndex(axis)]);
        bool positiveVisible = SceneViewportProjection.TryProjectWorldPointToViewport(context.ViewProjection,
            context.PivotPosition + worldDirection * targetLength, context.ViewportPos, context.ViewportSize, out Vector2 positiveEnd);
        bool negativeVisible = SceneViewportProjection.TryProjectWorldPointToViewport(context.ViewProjection,
            context.PivotPosition - worldDirection * targetLength, context.ViewportPos, context.ViewportSize, out Vector2 negativeEnd);
        int directionSign = cached.DirectionSign == 0 ? 1 : cached.DirectionSign;
        if (cached.DirectionSign == 0 || !HasActiveTransformDrag && !SurfaceDragState.IsDragging)
        {
            float lengthDifference = Vector2.DistanceSquared(positiveEnd, context.ScreenPos)
                - Vector2.DistanceSquared(negativeEnd, context.ScreenPos);
            if (positiveVisible != negativeVisible)
            {
                directionSign = positiveVisible ? 1 : -1;
            }
            else if (positiveVisible && !NumericsUtility.IsNearlyZero(lengthDifference))
            {
                directionSign = lengthDifference > 0f ? 1 : -1;
            }
        }

        worldDirection *= directionSign;
        Vector2? previousScreenDirection = cached.ScreenDirection;
        if (cached.DirectionSign != 0 && cached.DirectionSign != directionSign)
        {
            previousScreenDirection = -previousScreenDirection;
        }

        Vector2? projectedScreenDirection = null;
        var projectedScreenLength = 0f;
        if (directionSign > 0 ? positiveVisible : negativeVisible)
        {
            Vector2 projectedScreenEnd = directionSign > 0 ? positiveEnd : negativeEnd;
            var projectedScreenVector = projectedScreenEnd - context.ScreenPos;
            projectedScreenLength = projectedScreenVector.Length();
            if (NumericsUtility.HasLength(projectedScreenLength))
            {
                projectedScreenDirection = projectedScreenVector / projectedScreenLength;
            }
        }

        var fallbackScreenDirection = ResolveAxisFallbackScreenDirection(context, axis, projectedScreenDirection, previousScreenDirection);
        if (!NumericsUtility.HasLength(fallbackScreenDirection))
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

            var lengthBlend = projectedScreenLength < baseScreenLength && NumericsUtility.HasLength(baseScreenLength)
                ? Math.Clamp(1f - (projectedScreenLength / baseScreenLength), 0f, 1f)
                : 0f;
            var blend = MathF.Max(alignmentBlend, lengthBlend);
            if (blend > 0f)
            {
                var blendedDirection = Vector2.Lerp(drawScreenDirection, fallbackScreenDirection, blend);
                if (NumericsUtility.TryNormalize(blendedDirection, out var normalizedDirection))
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
            visualScale,
            directionSign);
        cached = new(directionSign, drawScreenDirection);
        return true;
    }

    private static float ResolveCompensatedLinearVisualScale(float projectedScreenLength, float drawScreenLength, float scale)
    {
        if (!NumericsUtility.HasLength(projectedScreenLength) || projectedScreenLength >= drawScreenLength)
        {
            return scale;
        }

        var ratio = drawScreenLength / MathF.Max(projectedScreenLength, 1f * scale);
        return scale * Math.Clamp(ratio, 1f, GizmoConstants.AxisMaxCompensatedHandleScale);
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
        var rotationMathProjection = projection.MathProjection;
        Vector2? previousPos = null;
        var previousVisible = false;

        for (var index = 0; index <= GizmoConstants.RotationRingSegments; ++index)
        {
            var currentAngle = (index / (float)GizmoConstants.RotationRingSegments) * (MathF.PI * 2f);
            if (!GizmoRotationMath.TryProjectAxisPoint(rotationMathProjection, axisDirection, currentAngle, out var currentPos, out var isVisible))
            {
                previousPos = null;
                previousVisible = false;
                continue;
            }

            if (previousPos.HasValue && previousVisible && isVisible)
            {
                var fromPos = previousPos.Value;
                var segment = currentPos - fromPos;
                var segmentLengthSq = segment.LengthSquared();
                if (NumericsUtility.HasLength(segmentLengthSq))
                {
                    var t = Vector2.Dot(mousePos - fromPos, segment) / segmentLengthSq;
                    t = Math.Clamp(t, 0f, 1f);
                    var projectedPoint = fromPos + (segment * t);
                    var projectedDistance = (mousePos - projectedPoint).Length();
                    if (projectedDistance < distance)
                    {
                        var previousAngle = ((index - 1) / (float)GizmoConstants.RotationRingSegments) * (MathF.PI * 2f);
                        distance = projectedDistance;
                        tangent = NumericsUtility.TryNormalize(segment, out var normalizedTangent)
                            ? normalizedTangent
                            : Vector2.UnitX;
                        screenPoint = projectedPoint;
                        angle = GizmoRotationMath.NormalizeAngle(previousAngle + ((currentAngle - previousAngle) * t));
                    }
                }
            }

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
        if (previousScreenDirection.HasValue && NumericsUtility.TryNormalize(previousScreenDirection.Value, out var normalizedPreviousDirection))
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
                && NumericsUtility.TryNormalize(context.CameraRight.Value - context.CameraUp.Value, out var diagonalDirection)
                    ? diagonalDirection
                    : null),
            _ => null,
        };

        if (fallbackDirection.HasValue && NumericsUtility.TryNormalize(fallbackDirection.Value, out var normalizedFallback))
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
        if (!worldDirection.HasValue || !NumericsUtility.HasLength(worldDirection.Value))
        {
            return null;
        }

        var projectedPoint = context.PivotPosition + (worldDirection.Value * context.AxisWorldLength);
        if (!SceneViewportProjection.TryProjectWorldPointToViewport(
                context.ViewProjection,
                projectedPoint,
                context.ViewportPos,
                context.ViewportSize,
                out var projectedScreenPoint))
        {
            return null;
        }

        var screenVector = projectedScreenPoint - context.ScreenPos;
        return !NumericsUtility.TryNormalize(screenVector, out var normalizedScreenVector)
            ? null
            : normalizedScreenVector;
    }

    private static void GetGizmoInteractionBounds(ReadOnlySpan<GizmoAxisVisualState> axes, Vector2 screenPos, float scale, out Vector2 min, out Vector2 max)
    {
        min = screenPos;
        max = screenPos;

        if (axes.Length == 0)
        {
            return;
        }

        var paddingScale = scale;
        foreach (GizmoAxisVisualState state in axes)
        {
            min = Vector2.Min(min, Vector2.Min(state.ScreenStart, state.ScreenEnd));
            max = Vector2.Max(max, Vector2.Max(state.ScreenStart, state.ScreenEnd));
            paddingScale = MathF.Max(paddingScale, state.VisualScale);
        }

        Vector2 padding = new(35f * paddingScale);
        min -= padding;
        max += padding;
    }

    private static Vector3 ResolveAxisWorldDirection(GizmoAxis axis, Quaternion rotation, bool useWorldSpace)
    {
        var axisDirection = GizmoAxisUtility.ToUnitVector(axis);
        if (!NumericsUtility.HasLength(axisDirection) || useWorldSpace)
        {
            return axisDirection;
        }

        var rotated = Vector3.Transform(axisDirection, rotation);
        return !NumericsUtility.TryNormalize(rotated, out var normalizedRotated)
            ? axisDirection
            : normalizedRotated;
    }

    private static Vector2 ResolveFallbackZScreenDirection()
        => NumericsUtility.TryNormalize(new Vector2(0.85f, -0.65f), out var direction)
            ? direction
            : Vector2.UnitX;
}
