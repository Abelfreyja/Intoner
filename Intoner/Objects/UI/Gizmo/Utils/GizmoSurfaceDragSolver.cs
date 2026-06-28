using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.UI;

internal readonly record struct GizmoSurfaceDragSingleResult(ObjectTransform Transform, Quaternion RotationQuaternion);

internal readonly record struct GizmoSurfaceDragSelectionResult(Vector3 PivotPosition, Quaternion GroupRotation);

internal static class GizmoSurfaceDragSolver
{
    public static Quaternion ResolveRotationSteps(
        Quaternion startRotation,
        IReadOnlyList<GizmoSurfaceDragRotationStep> rotationSteps,
        Vector3? surfaceNormal,
        Vector3? cameraRight)
        => ResolveRotationSequence(startRotation, rotationSteps, surfaceNormal, cameraRight);

    public static GizmoSurfaceDragSingleResult ResolveSingle(
        ObjectSurfaceHit hit,
        ObjectSnapshot startSnapshot,
        Quaternion startRotationQuaternion,
        IReadOnlyList<GizmoSurfaceDragRotationStep> rotationSteps,
        Vector3? cameraRight,
        in GizmoSelectionEntry primaryEntry,
        bool usePlacementOrigin,
        bool alignToSurfaceNormal)
    {
        var rotationQuaternion = ObjectTransformMath.NormalizeQuaternion(startRotationQuaternion);
        var position = hit.Point;

        if (TryResolveSurfaceNormal(hit, out var surfaceNormal))
        {
            Vector3? rotationSurfaceNormal = alignToSurfaceNormal ? surfaceNormal : null;
            if (alignToSurfaceNormal)
            {
                rotationQuaternion = ObjectTransformMath.AlignUpToNormal(rotationQuaternion, surfaceNormal);
            }

            rotationQuaternion = ResolveRotationSequence(rotationQuaternion, rotationSteps, rotationSurfaceNormal, cameraRight);
            if (!usePlacementOrigin)
            {
                var minimumSupport = ResolveEntrySurfaceSupport(primaryEntry, rotationQuaternion, surfaceNormal);
                position = hit.Point - (surfaceNormal * minimumSupport);
            }
        }
        else
        {
            rotationQuaternion = ResolveRotationSequence(rotationQuaternion, rotationSteps, null, cameraRight);
        }

        return new GizmoSurfaceDragSingleResult(
            startSnapshot.Transform with
            {
                Position = position,
            },
            rotationQuaternion);
    }

    public static bool TryResolveSelection(
        ObjectSurfaceHit hit,
        Quaternion startRotationQuaternion,
        IReadOnlyList<GizmoSelectionEntry> selectionEntries,
        bool alignToSurfaceNormal,
        IReadOnlyList<GizmoSurfaceDragRotationStep> rotationSteps,
        Vector3? cameraRight,
        out GizmoSurfaceDragSelectionResult result)
    {
        result = default;
        if (selectionEntries.Count == 0)
        {
            return false;
        }

        var pivotPosition = hit.Point;
        var groupRotation = ObjectTransformMath.NormalizeQuaternion(startRotationQuaternion);
        if (TryResolveSurfaceNormal(hit, out var surfaceNormal))
        {
            Vector3? rotationSurfaceNormal = alignToSurfaceNormal ? surfaceNormal : null;
            if (alignToSurfaceNormal)
            {
                groupRotation = ObjectTransformMath.AlignUpToNormal(groupRotation, surfaceNormal);
            }

            groupRotation = ResolveRotationSequence(
                groupRotation,
                rotationSteps,
                rotationSurfaceNormal,
                cameraRight);

            var groupDelta = GizmoSelectionTransformUtility.ResolveRotationDelta(startRotationQuaternion, groupRotation);
            var supportOffset = ResolveSelectionSurfaceSupportOffset(selectionEntries, groupDelta, surfaceNormal);
            pivotPosition = hit.Point - (surfaceNormal * supportOffset);
        }
        else
        {
            groupRotation = ResolveRotationSequence(
                groupRotation,
                rotationSteps,
                null,
                cameraRight);
        }

        result = new GizmoSurfaceDragSelectionResult(pivotPosition, groupRotation);
        return true;
    }

    private static Quaternion ResolveRotationSequence(
        Quaternion startRotation,
        IReadOnlyList<GizmoSurfaceDragRotationStep> rotationSteps,
        Vector3? surfaceNormal,
        Vector3? cameraRight)
    {
        var rotation = ObjectTransformMath.NormalizeQuaternion(startRotation);

        foreach (var step in rotationSteps)
        {
            rotation = ApplyRotationStep(rotation, step, surfaceNormal, cameraRight);
        }

        return rotation;
    }

    private static Quaternion ApplyRotationStep(
        Quaternion rotation,
        GizmoSurfaceDragRotationStep step,
        Vector3? surfaceNormal,
        Vector3? cameraRight)
    {
        if (step.StepCount == 0)
        {
            return ObjectTransformMath.NormalizeQuaternion(rotation);
        }

        var currentRotation = ObjectTransformMath.NormalizeQuaternion(rotation);
        var stepDirection = Math.Sign(step.StepCount);
        var stepCount = Math.Abs(step.StepCount);
        for (var index = 0; index < stepCount; ++index)
        {
            if (!ObjectSelectionTransformMath.TryResolveSurfaceDragRotationAxes(currentRotation, surfaceNormal, cameraRight, out var yawAxis, out var pitchAxis))
            {
                break;
            }

            currentRotation = ObjectSelectionTransformMath.ApplySurfaceDragRotationStep(
                currentRotation,
                step.Axis == GizmoSurfaceDragRotationAxis.Yaw,
                stepDirection,
                GizmoConstants.SurfaceDragRotateStepDegrees,
                yawAxis,
                pitchAxis);
        }

        return currentRotation;
    }

    private static bool TryResolveSurfaceNormal(ObjectSurfaceHit hit, out Vector3 surfaceNormal)
    {
        surfaceNormal = Vector3.UnitY;
        return ObjectMathUtility.HasLength(hit.Normal)
               && ObjectMathUtility.TryNormalize(hit.Normal, out surfaceNormal);
    }

    private static float ResolveSelectionSurfaceSupportOffset(
        IReadOnlyList<GizmoSelectionEntry> selectionEntries,
        Quaternion groupDelta,
        Vector3 surfaceNormal)
    {
        var minimumSupport = float.PositiveInfinity;
        foreach (var entry in selectionEntries)
        {
            var rotatedPivotOffset = Vector3.Transform(entry.PivotOffset, groupDelta);
            var entryRotation = ObjectTransformMath.NormalizeQuaternion(groupDelta * entry.StartRotationQuaternion);
            var support = Vector3.Dot(rotatedPivotOffset, surfaceNormal) + ResolveEntrySurfaceSupport(entry, entryRotation, surfaceNormal);
            minimumSupport = MathF.Min(minimumSupport, support);
        }

        return float.IsFinite(minimumSupport) ? minimumSupport : 0f;
    }

    private static float ResolveEntrySurfaceSupport(GizmoSelectionEntry entry, Quaternion rotation, Vector3 surfaceNormal)
    {
        if (!entry.HasBoundsData)
        {
            return 0f;
        }

        var centerOffset = Vector3.Transform(entry.BoundsCenterLocalOffset, rotation);
        var supportExtent = ObjectShapeMath.ComputeOrientedBoundsSupportExtent(rotation, entry.BoundsHalfExtents, surfaceNormal);
        return Vector3.Dot(centerOffset, surfaceNormal) - supportExtent;
    }
}

