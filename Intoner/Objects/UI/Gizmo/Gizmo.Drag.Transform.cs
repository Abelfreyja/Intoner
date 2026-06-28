using Dalamud.Bindings.ImGui;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.Utils;
using System;
using System.Globalization;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class Gizmo
{
    private static float GetGizmoDragSpeedMultiplier()
        => GizmoInputUtility.GetGizmoDragSpeedMultiplier(GizmoConstants.SlowDragMultiplier);

    private bool HandleActiveGizmoDragLifecycle(
        bool matchesCurrentTarget,
        bool captureKeyboard,
        Action updateDrag,
        Action completeDrag)
    {
        if (IsGizmoWheelOpen())
        {
            if (matchesCurrentTarget)
            {
                completeDrag();
            }

            return false;
        }

        if (!matchesCurrentTarget)
        {
            return false;
        }

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            completeDrag();
            return false;
        }

        updateDrag();
        ImGui.SetNextFrameWantCaptureMouse(true);
        if (captureKeyboard)
        {
            ImGui.SetNextFrameWantCaptureKeyboard(true);
        }

        return true;
    }

    private bool HandleGizmoDragLifecycle(in GizmoContext context, GizmoTransformMode mode)
    {
        var matchesCurrentTarget = TryGetMatchingTransformDragState(context.PrimarySnapshot.Id, mode, out var dragState);
        if (!HandleActiveGizmoDragLifecycle(matchesCurrentTarget, false, () => UpdateGizmoDrag(dragState!), CompleteGizmoDrag))
        {
            return false;
        }

        DrawGizmoDragMetrics(context.ScreenPos);
        return true;
    }

    private void UpdateGizmoDrag(GizmoTransformDragSession dragState)
    {
        switch (dragState)
        {
            case GizmoTranslationDragSession translationDragState:
                UpdateTranslationGizmoDrag(translationDragState);
                break;
            case GizmoRotationDragSession rotationDragState:
                UpdateRotationGizmoDrag(rotationDragState);
                break;
            case GizmoScaleDragSession scaleDragState:
                UpdateScaleGizmoDrag(scaleDragState);
                break;
        }
    }

    private bool TryBuildGizmoMetricInfo(out GizmoMetricInfo info)
    {
        info = default;
        if (!TryGetActiveTransformDragState(out var dragState) || dragState.ActiveAxis == GizmoAxis.None)
        {
            return false;
        }

        var axisLabel = AxisLabel(dragState.ActiveAxis);
        var deltaText = dragState.Mode switch
        {
            GizmoTransformMode.Translation => $"{axisLabel} Δ {ResolveTranslationMetricValue():+0.000;-0.000;0.000}",
            GizmoTransformMode.Rotation => $"{axisLabel} Δ {(RotationDragState.RotationDragAppliedRadians * (180f / MathF.PI)):+0.0;-0.0;0.0}°",
            GizmoTransformMode.Scale => $"{axisLabel} Δ {ResolveScaleMetricValue():+0.000;-0.000;0.000}",
            _ => null,
        };
        if (string.IsNullOrEmpty(deltaText))
        {
            return false;
        }

        info = new GizmoMetricInfo(deltaText, BuildCurrentTransformMetricValueText(dragState));
        return true;
    }

    private static string? BuildCurrentTransformMetricValueText(GizmoTransformDragSession dragState)
    {
        var currentSnapshot = dragState.TryResolveAppliedSnapshot(out var appliedSnapshot)
            ? appliedSnapshot
            : dragState.StartSnapshot;
        return dragState.Mode switch
        {
            GizmoTransformMode.Translation => $"pos {FormatMetricVector(currentSnapshot.Transform.Position, "0.000")}",
            GizmoTransformMode.Rotation => $"rot {FormatMetricVector(currentSnapshot.Transform.RotationDegrees, "0.0")}",
            GizmoTransformMode.Scale => $"scale {FormatMetricVector(currentSnapshot.Transform.Scale, "0.000")}",
            _ => null,
        };
    }

    private static string FormatMetricVector(Vector3 value, string componentFormat)
        => value.X.ToString(componentFormat, CultureInfo.InvariantCulture)
         + ", "
         + value.Y.ToString(componentFormat, CultureInfo.InvariantCulture)
         + ", "
         + value.Z.ToString(componentFormat, CultureInfo.InvariantCulture);

    private float ResolveTranslationMetricValue()
    {
        if (!ObjectMathUtility.TryNormalize(TranslationDragState.AxisWorldDirection, out var axisWorldDirection))
        {
            return 0f;
        }

        return Vector3.Dot(
            TranslationDragState.LastPosition - TranslationDragState.StartPosition,
            axisWorldDirection);
    }

    private float ResolveScaleMetricValue()
    {
        var axisIndex = GizmoAxisUtility.ToIndex(ScaleDragState.ActiveAxis);
        if (axisIndex < 0)
        {
            return 0f;
        }

        return axisIndex switch
        {
            0 => ScaleDragState.LastScale.X - ScaleDragState.StartScale.X,
            1 => ScaleDragState.LastScale.Y - ScaleDragState.StartScale.Y,
            2 => ScaleDragState.LastScale.Z - ScaleDragState.StartScale.Z,
            _ => 0f,
        };
    }

    private static bool TryBuildCurrentMouseRay(
        Vector2 viewportPos,
        Vector2 viewportSize,
        out Vector3 rayOrigin,
        out Vector3 rayDirection)
        => ObjectSurfaceRaycastUtility.TryBuildScreenRay(viewportPos, viewportSize, ImGui.GetIO().MousePos, out rayOrigin, out rayDirection);

    private static bool HasDragValueChanged(Vector3 nextValue, Vector3 lastValue)
        => ObjectMathUtility.HasMeaningfulChange(nextValue, lastValue);

    private static bool HasSurfaceDragTransformChanged(
        Vector3 nextPosition,
        Vector3 lastPosition,
        Vector3 nextRotation,
        Vector3 lastRotation)
        => HasDragValueChanged(nextPosition, lastPosition)
           || HasDragValueChanged(nextRotation, lastRotation);

    private bool TryApplySelectionDragChange(
        GizmoTransformDragSession dragState,
        Vector3 nextValue,
        Vector3 lastValue,
        Func<GizmoSelectionEntry, ObjectTransform> transformFactory)
        => HasDragValueChanged(nextValue, lastValue)
           && TryApplyDragSelectionTransforms(dragState, transformFactory);

    private bool TryApplySingleDragChange(
        GizmoTransformDragSession dragState,
        Vector3 nextValue,
        Vector3 lastValue,
        ObjectTransform transform)
        => HasDragValueChanged(nextValue, lastValue)
           && TryApplyDragTransform(dragState, transform);

    private bool TryApplyDragTransform(GizmoTransformDragSession dragState, ObjectTransform transform)
    {
        if (!_mutationService.TryUpdate(dragState.StartSnapshot with { Transform = transform }, out var appliedSnapshot))
        {
            return false;
        }

        dragState.RecordAppliedSnapshot(appliedSnapshot);
        return true;
    }

    private bool TryApplyDragSelectionTransforms(
        GizmoTransformDragSession dragState,
        Func<GizmoSelectionEntry, ObjectTransform> transformFactory)
    {
        var selectionEntries = dragState.SelectionEntries;
        var snapshots = new ObjectSnapshot[selectionEntries.Length];
        for (var index = 0; index < selectionEntries.Length; ++index)
        {
            var entry = selectionEntries[index];
            snapshots[index] = entry.Snapshot with { Transform = transformFactory(entry) };
        }

        if (!_mutationService.TryUpdateMany(snapshots, out var appliedSnapshots))
        {
            return false;
        }

        dragState.RecordAppliedSnapshots(appliedSnapshots);
        return true;
    }

    private bool TryRecordGizmoHistoryAction(
        ObjectHistoryKind kind,
        string title,
        IReadOnlyList<ObjectSnapshot> beforeSnapshots,
        IReadOnlyList<ObjectSnapshot> afterSnapshots)
    {
        var selectionIds = _host.CaptureCurrentSelectionIds();
        return _host.TryRecordCompletedHistoryAction(
            kind,
            title,
            beforeSnapshots,
            afterSnapshots,
            selectionIds,
            selectionIds);
    }

    private void CompleteGizmoDrag()
    {
        if (TryGetActiveTransformDragState(out var dragState))
        {
            TryCommitGizmoDragHistory(dragState);
        }

        ResetGizmoDrag();
    }

    private void ResetGizmoDrag()
        => State.ResetTransformDragSessions();

    private void CompleteGizmoSurfaceDrag()
    {
        TryCommitGizmoSurfaceDragHistory();
        ResetGizmoSurfaceDrag();
    }

    private void ResetGizmoSurfaceDrag()
    {
        DisposeSurfaceDragInputSuppressionLease();
        State.ResetSurfaceDrag();
    }

    private void TryCommitGizmoDragHistory(GizmoTransformDragSession dragState)
    {
        if (!dragState.TryGetHistorySnapshots(out var beforeSnapshots, out var afterSnapshots))
        {
            return;
        }

        var (kind, title) = ResolveGizmoDragHistoryMetadata(dragState.Mode, beforeSnapshots.Length);
        _ = TryRecordGizmoHistoryAction(kind, title, beforeSnapshots, afterSnapshots);
    }

    private void TryCommitGizmoSurfaceDragHistory()
    {
        if (!SurfaceDragState.TryGetHistorySnapshots(out var beforeSnapshots, out var afterSnapshots))
        {
            return;
        }

        var title = beforeSnapshots.Length == 1 ? "Move Object On Surface" : "Move Objects On Surface";
        _ = TryRecordGizmoHistoryAction(ObjectHistoryKind.Move, title, beforeSnapshots, afterSnapshots);
    }

    private static (ObjectHistoryKind Kind, string Title) ResolveGizmoDragHistoryMetadata(GizmoTransformMode mode, int selectionCount)
        => mode switch
        {
            GizmoTransformMode.Translation => (
                ObjectHistoryKind.Move,
                selectionCount == 1 ? "Move Object" : "Move Objects"),
            GizmoTransformMode.Rotation => (
                ObjectHistoryKind.Transform,
                selectionCount == 1 ? "Rotate Object" : "Rotate Objects"),
            GizmoTransformMode.Scale => (
                ObjectHistoryKind.Transform,
                selectionCount == 1 ? "Scale Object" : "Scale Objects"),
            _ => (
                ObjectHistoryKind.Transform,
                selectionCount == 1 ? "Transform Object" : "Transform Objects"),
        };

    private void DisposeSurfaceDragInputSuppressionLease()
    {
        SurfaceDragInputSuppressionLease?.Dispose();
        SurfaceDragInputSuppressionLease = null;
    }

    private void BeginLinearGizmoDrag(in GizmoContext context, GizmoAxis axis, GizmoAxisVisualState state, GizmoTransformMode mode)
    {
        var dragScreenLength = GizmoTranslationMath.ResolveDragScreenLength(
            context.ViewProjection,
            context.PivotPosition,
            context.ScreenPos,
            context.ViewportPos,
            context.ViewportSize,
            state.WorldDirection,
            state.WorldLength,
            state.ScreenLength);

        State.ResetTransformDragSessions();
        if (mode == GizmoTransformMode.Translation)
        {
            TranslationDragState.Begin(
                context.SelectedSnapshots,
                context.PrimarySnapshot,
                context.PivotPosition,
                context.Rotation,
                axis,
                state.ScreenDirection,
                dragScreenLength,
                state.WorldDirection,
                state.WorldLength,
                context.UseWorldSpace);
            CaptureTranslationGizmoDragPlane(context, state.WorldDirection);
            return;
        }

        ScaleDragState.Begin(
            context.SelectedSnapshots,
            context.PrimarySnapshot,
            context.PivotPosition,
            axis,
            state.ScreenDirection,
            dragScreenLength,
            state.WorldDirection,
            state.WorldLength);
    }

    private void CaptureTranslationGizmoDragPlane(in GizmoContext context, Vector3 axisWorldDirection)
    {
        if (!GizmoTranslationMath.TryResolveDragPlaneNormal(
                axisWorldDirection,
                context.CameraViewDirection,
                context.CameraRight,
                context.CameraUp,
                out var planeNormal)
            || !TryBuildCurrentMouseRay(context.ViewportPos, context.ViewportSize, out var rayOrigin, out var rayDirection)
            || !ObjectSurfaceRaycastUtility.TryIntersectRayPlane(rayOrigin, rayDirection, context.PivotPosition, planeNormal, out var startPlanePoint))
        {
            return;
        }

        TranslationDragState.SetTranslationPlane(context.PivotPosition, planeNormal, startPlanePoint, context.ViewportPos, context.ViewportSize);
    }

    private void UpdateTranslationGizmoDrag(GizmoTranslationDragSession dragState)
    {
        if (!ObjectMathUtility.HasLength(dragState.AxisWorldLength))
        {
            return;
        }

        var axisIndex = GizmoAxisUtility.ToIndex(dragState.ActiveAxis);
        if (axisIndex < 0)
        {
            return;
        }

        var newPosition = TryResolveTranslationGizmoDragPosition(dragState, out var resolvedPosition)
            ? resolvedPosition
            : ResolveTranslationGizmoDragPositionFromScreen(dragState);

        var snapPolicy = ResolveTranslationDragSnapPolicy(newPosition);
        newPosition = snapPolicy.SnapPositionAxis(newPosition, axisIndex);

        var worldDelta = newPosition - dragState.StartPosition;
        if (!TryApplySelectionDragChange(dragState, newPosition, dragState.LastPosition, entry => entry.Snapshot.Transform with
            {
                Position = entry.Snapshot.Transform.Position + worldDelta,
            }))
        {
            return;
        }

        dragState.RecordAppliedPosition(newPosition);
    }

    private bool TryResolveTranslationGizmoDragPosition(GizmoTranslationDragSession dragState, out Vector3 position)
    {
        position = default;
        if (!dragState.TranslationPlane.HasValue)
        {
            return false;
        }

        var translationPlane = dragState.TranslationPlane.Value;
        if (!TryBuildCurrentMouseRay(translationPlane.ViewportPos, translationPlane.ViewportSize, out var rayOrigin, out var rayDirection)
            || !ObjectSurfaceRaycastUtility.TryIntersectRayPlane(rayOrigin, rayDirection, translationPlane.PlanePoint, translationPlane.PlaneNormal, out var planePoint))
        {
            return false;
        }

        var planeDelta = planePoint - translationPlane.PlaneStartPoint;
        var axisDistance = Vector3.Dot(planeDelta, dragState.AxisWorldDirection) * GetGizmoDragSpeedMultiplier();
        position = dragState.StartPosition + (dragState.AxisWorldDirection * axisDistance);
        return true;
    }

    private Vector3 ResolveTranslationGizmoDragPositionFromScreen(GizmoTranslationDragSession dragState)
    {
        if (!ObjectMathUtility.HasLength(dragState.AxisScreenLength))
        {
            return dragState.StartPosition;
        }

        var mouseDelta = ImGui.GetIO().MousePos - dragState.StartMouse;
        var axisPixels = Vector2.Dot(mouseDelta, dragState.AxisScreenDirection) * GetGizmoDragSpeedMultiplier();
        if (ObjectMathUtility.IsNearlyZero(axisPixels))
        {
            return dragState.StartPosition;
        }

        var pixelsToWorld = dragState.AxisWorldLength / dragState.AxisScreenLength;
        var axisWorldDelta = dragState.AxisWorldDirection * (axisPixels * pixelsToWorld);
        return dragState.StartPosition + axisWorldDelta;
    }

    private void BeginRotationGizmoDrag(
        in GizmoContext context,
        GizmoAxis axis,
        RotationHoverState hoverState,
        in RotationProjectionContext projection)
    {
        State.ResetTransformDragSessions();
        RotationDragState.Begin(
            context.SelectedSnapshots,
            context.PrimarySnapshot,
            context.PivotPosition,
            context.Rotation,
            axis,
            hoverState.Tangent,
            context.UseWorldSpace,
            hoverState.Angle,
            projection);
    }

    private static RotationProjectionContext CreateRotationProjectionContext(in GizmoContext context, float screenRadius)
    {
        var referenceWorldRadius = Math.Max(context.AxisWorldLength, GizmoConstants.RotationProjectionMinReferenceWorldRadius);
        var projection = new GizmoRotationMath.Projection(
            context.ScreenPos,
            screenRadius,
            referenceWorldRadius,
            context.PivotPosition,
            context.ViewProjection,
            context.ViewportPos,
            context.ViewportSize,
            context.CameraViewDirection,
            context.CameraRight,
            context.CameraUp);

        var rotationProjection = new RotationProjectionContext(
            context.ScreenPos,
            screenRadius,
            screenRadius,
            GizmoRotationMath.ResolveWorldRadius(projection, referenceWorldRadius),
            context.PivotPosition,
            context.ViewProjection,
            context.ViewportPos,
            context.ViewportSize,
            context.Rotation,
            context.UseWorldSpace,
            context.CameraViewDirection,
            context.CameraRight,
            context.CameraUp);

        return ResolveRotationProjectionSize(rotationProjection);
    }

    private static GizmoRotationMath.Projection CreateRotationMathProjection(in RotationProjectionContext projection)
    {
        return new GizmoRotationMath.Projection(
            projection.Center,
            projection.ScreenRadius,
            projection.WorldRadius,
            projection.WorldPosition,
            projection.ViewProjection,
            projection.ViewportPos,
            projection.ViewportSize,
            projection.CameraViewDirection,
            projection.CameraRight,
            projection.CameraUp);
    }

    private static RotationProjectionContext ResolveRotationProjectionSize(in RotationProjectionContext projection)
    {
        var resolvedProjection = projection;
        for (var iteration = 0; iteration < GizmoConstants.RotationProjectionSizeSolveIterations; ++iteration)
        {
            if (!TryResolveProjectedRotationRadius(resolvedProjection, out var projectedRadius))
            {
                break;
            }

            var radiusScale = resolvedProjection.ScreenRadius / projectedRadius;
            if (!float.IsFinite(radiusScale) || radiusScale <= 0f || ObjectMathUtility.IsNearlyEqual(radiusScale, 1f, 0.01f))
            {
                break;
            }

            var worldRadius = resolvedProjection.WorldRadius * radiusScale;
            if (!float.IsFinite(worldRadius) || worldRadius <= 0f)
            {
                break;
            }

            resolvedProjection = resolvedProjection with
            {
                WorldRadius = worldRadius,
            };
        }

        return ResolveRotationVisualRadius(resolvedProjection);
    }

    private static RotationProjectionContext ResolveRotationVisualRadius(in RotationProjectionContext projection)
    {
        return TryResolveProjectedRotationRadius(projection, out var visualRadius)
            ? projection with { VisualRadius = MathF.Max(projection.ScreenRadius, visualRadius) }
            : projection with { VisualRadius = projection.ScreenRadius };
    }

    private static bool TryResolveProjectedRotationRadius(in RotationProjectionContext projection, out float radius)
    {
        radius = 0f;
        var rotationMathProjection = CreateRotationMathProjection(projection);
        for (var index = 0; index < GizmoAxisUtility.AxisCount; ++index)
        {
            var axis = GizmoAxisUtility.FromIndex(index);
            var axisDirection = ResolveAxisWorldDirection(axis, projection.Rotation, projection.UseWorldSpace);
            for (var segment = 0; segment < GizmoConstants.RotationRingSegments; ++segment)
            {
                var angle = segment / (float)GizmoConstants.RotationRingSegments * MathF.Tau;
                if (!GizmoRotationMath.TryProjectAxisPoint(rotationMathProjection, axisDirection, angle, out var point, out _))
                {
                    continue;
                }

                var distance = (point - projection.Center).Length();
                if (float.IsFinite(distance))
                {
                    radius = MathF.Max(radius, distance);
                }
            }
        }

        return radius > float.Epsilon;
    }

    private void UpdateRotationGizmoDrag(GizmoRotationDragSession dragState)
    {
        if (!TryResolveRotationDragDeltaRadians(dragState, out var radiansDelta))
        {
            return;
        }

        var snapPolicy = ResolveTranslationDragSnapPolicy(dragState.StartPosition);
        var snappedDegrees = snapPolicy.SnapRotationDegrees(radiansDelta * (180f / MathF.PI));
        radiansDelta = snappedDegrees * (MathF.PI / 180f);

        var startRotationQuaternion = ObjectTransformMath.NormalizeQuaternion(dragState.StartRotationQuaternion);
        var localDeltaQuaternion = ObjectTransformMath.NormalizeQuaternion(Quaternion.CreateFromAxisAngle(GizmoAxisUtility.ToUnitVector(dragState.ActiveAxis), radiansDelta));
        var groupDeltaQuaternion = dragState.UseWorldSpace
            ? localDeltaQuaternion
            : ResolveLocalRotationDeltaQuaternion(dragState.ActiveAxis, startRotationQuaternion, radiansDelta);

        if (ObjectMathUtility.IsNearlyZero(radiansDelta - dragState.RotationDragAppliedRadians)
            || !TryApplyDragSelectionTransforms(dragState, entry =>
                GizmoSelectionTransformUtility.ApplyRigidRotation(
                    entry,
                    dragState.StartPosition,
                    groupDeltaQuaternion,
                    dragState.ResolveReferenceRotationDegrees(entry.Snapshot.Id))))
        {
            return;
        }

        dragState.RecordAppliedRotation(radiansDelta);
    }

    private static Quaternion ResolveLocalRotationDeltaQuaternion(GizmoAxis axis, Quaternion startRotationQuaternion, float radiansDelta)
    {
        Vector3 worldAxis = Vector3.Transform(GizmoAxisUtility.ToUnitVector(axis), startRotationQuaternion);
        return ObjectMathUtility.TryNormalize(worldAxis, out worldAxis)
            ? ObjectTransformMath.NormalizeQuaternion(Quaternion.CreateFromAxisAngle(worldAxis, radiansDelta))
            : Quaternion.Identity;
    }

    private bool TryResolveRotationDragDeltaRadians(GizmoRotationDragSession dragState, out float radiansDelta)
    {
        radiansDelta = 0f;
        if (!dragState.RotationProjection.HasValue
            || dragState.ActiveAxis == GizmoAxis.None
            || !dragState.RotationDragStartAngle.HasValue)
        {
            return false;
        }

        if (!TryResolveRotationRingDeltaRadians(dragState, out var stepRadians))
        {
            return false;
        }

        dragState.AccumulateRadians(stepRadians);
        radiansDelta = dragState.RotationDragAccumulatedRadians;
        return true;
    }

    private bool TryResolveRotationRingDeltaRadians(GizmoRotationDragSession dragState, out float radiansDelta)
    {
        radiansDelta = 0f;

        var referenceAngle = dragState.RotationDragLastAngle ?? dragState.RotationDragStartAngle;
        var rotationProjection = dragState.RotationProjection.GetValueOrDefault();
        var axisDirection = ResolveAxisWorldDirection(dragState.ActiveAxis, rotationProjection.Rotation, rotationProjection.UseWorldSpace);
        var mousePos = ImGui.GetIO().MousePos;
        if (!GizmoRotationMath.TryFindClosestRingAngle(
                CreateRotationMathProjection(rotationProjection),
                axisDirection,
                mousePos,
                referenceAngle,
                GizmoConstants.RotationRingSegments,
                out var currentAngle))
        {
            return false;
        }

        var priorAngle = dragState.RotationDragLastAngle ?? dragState.RotationDragStartAngle;
        if (!priorAngle.HasValue)
        {
            dragState.RecordCurrentStep(currentAngle, mousePos);
            return false;
        }

        var angleStep = GizmoRotationMath.SignedAngleDelta(priorAngle.Value, currentAngle);
        if (TryResolveRotationScreenTangentSign(rotationProjection, axisDirection, priorAngle.Value, mousePos - dragState.RotationDragLastMouse, out var screenSign)
            && screenSign != 0
            && MathF.Sign(angleStep) != screenSign)
        {
            angleStep = -angleStep;
        }

        var maxAngleStep = MathF.PI / 6f;
        if (MathF.Abs(angleStep) > maxAngleStep)
        {
            angleStep = MathF.Sign(angleStep) * maxAngleStep;
        }

        var pixelStep = angleStep * rotationProjection.ScreenRadius;
        var step = pixelStep * GizmoConstants.RotationRadiansPerPixel * GetGizmoDragSpeedMultiplier();
        radiansDelta = step;
        dragState.RecordCurrentStep(currentAngle, mousePos);
        return true;
    }

    private static bool TryResolveRotationScreenTangentSign(
        in RotationProjectionContext projection,
        Vector3 axisDirection,
        float referenceAngle,
        Vector2 mouseDelta,
        out int sign)
    {
        sign = 0;
        if (!ObjectMathUtility.HasLength(mouseDelta))
        {
            return false;
        }

        var tangentStep = MathF.Tau / GizmoConstants.RotationRingSegments;
        var rotationProjection = CreateRotationMathProjection(projection);
        var startPoint = GizmoRotationMath.ProjectAxisPoint(rotationProjection, axisDirection, referenceAngle);
        var nextPoint = GizmoRotationMath.ProjectAxisPoint(rotationProjection, axisDirection, referenceAngle + tangentStep);
        var tangent = nextPoint - startPoint;
        if (!ObjectMathUtility.TryNormalize(tangent, out var normalizedTangent))
        {
            return false;
        }

        var tangentStepDelta = Vector2.Dot(mouseDelta, normalizedTangent);
        if (ObjectMathUtility.IsNearlyZero(tangentStepDelta))
        {
            return false;
        }

        sign = MathF.Sign(tangentStepDelta);
        return true;
    }

    private void UpdateScaleGizmoDrag(GizmoScaleDragSession dragState)
    {
        if (!ObjectMathUtility.HasLength(dragState.AxisScreenLength))
        {
            return;
        }

        var axisIndex = GizmoAxisUtility.ToIndex(dragState.ActiveAxis);
        if (axisIndex < 0)
        {
            return;
        }

        var mouseDelta = ImGui.GetIO().MousePos - dragState.StartMouse;
        var axisPixels = Vector2.Dot(mouseDelta, dragState.AxisScreenDirection);
        axisPixels *= GetGizmoDragSpeedMultiplier();

        var scaleDelta = axisPixels * GizmoConstants.ScaleUnitsPerPixel;
        if (ObjectMathUtility.IsNearlyZero(scaleDelta))
        {
            return;
        }

        var newScale = dragState.StartScale;
        newScale = axisIndex switch
        {
            0 => new Vector3(MathF.Max(0.01f, newScale.X + scaleDelta), newScale.Y, newScale.Z),
            1 => new Vector3(newScale.X, MathF.Max(0.01f, newScale.Y + scaleDelta), newScale.Z),
            2 => new Vector3(newScale.X, newScale.Y, MathF.Max(0.01f, newScale.Z + scaleDelta)),
            _ => newScale,
        };
        newScale = ResolveTranslationDragSnapPolicy(dragState.StartSnapshot.Transform.Position).SnapScale(newScale);

        if (!TryApplySingleDragChange(dragState, newScale, dragState.LastScale, dragState.StartSnapshot.Transform with { Scale = newScale }))
        {
            return;
        }

        dragState.RecordAppliedScale(newScale);
    }
}

