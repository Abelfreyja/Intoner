using Intoner.Scene;
using Intoner.Objects.Utils;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct GizmoSurfaceDragSingleResult(SceneTransform Transform, Quaternion RotationQuaternion);

[StructLayout(LayoutKind.Auto)]
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
        SceneSurfaceHit hit,
        SceneItemSnapshot startSnapshot,
        Quaternion startRotationQuaternion,
        IReadOnlyList<GizmoSurfaceDragRotationStep> rotationSteps,
        Vector3? cameraRight,
        in GizmoSelectionEntry primaryEntry,
        Vector3 surfaceAlignmentAxis,
        bool usePlacementOrigin,
        bool forceSurfaceAlignment,
        bool alignToSurfaceNormal)
    {
        var rotationQuaternion = SceneTransformMath.NormalizeQuaternion(startRotationQuaternion);
        var position = hit.Point;

        if (TryResolveSurfaceNormal(hit, out var surfaceNormal))
        {
            bool shouldAlignToSurface = alignToSurfaceNormal || forceSurfaceAlignment;
            Vector3? rotationSurfaceNormal = shouldAlignToSurface ? surfaceNormal : null;
            if (shouldAlignToSurface)
            {
                Vector3 alignmentAxis = NumericsUtility.TryNormalize(surfaceAlignmentAxis, out Vector3 normalizedAxis)
                    ? normalizedAxis
                    : Vector3.UnitY;
                rotationQuaternion = SceneTransformMath.AlignLocalAxisToDirection(
                    rotationQuaternion,
                    alignmentAxis,
                    surfaceNormal);
            }

            rotationQuaternion = ResolveRotationSequence(rotationQuaternion, rotationSteps, rotationSurfaceNormal, cameraRight);
            if (!forceSurfaceAlignment && !usePlacementOrigin)
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
        SceneSurfaceHit hit,
        Quaternion startRotationQuaternion,
        IReadOnlyList<GizmoSelectionEntry> selectionEntries,
        Vector3 surfaceAlignmentAxis,
        bool forceSurfaceAlignment,
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
        var groupRotation = SceneTransformMath.NormalizeQuaternion(startRotationQuaternion);
        if (TryResolveSurfaceNormal(hit, out var surfaceNormal))
        {
            bool shouldAlignToSurface = alignToSurfaceNormal || forceSurfaceAlignment;
            Vector3? rotationSurfaceNormal = shouldAlignToSurface ? surfaceNormal : null;
            if (shouldAlignToSurface)
            {
                Vector3 alignmentAxis = NumericsUtility.TryNormalize(surfaceAlignmentAxis, out Vector3 normalizedAxis)
                    ? normalizedAxis
                    : Vector3.UnitY;
                groupRotation = SceneTransformMath.AlignLocalAxisToDirection(
                    groupRotation,
                    alignmentAxis,
                    surfaceNormal);
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
        var rotation = SceneTransformMath.NormalizeQuaternion(startRotation);

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
            return SceneTransformMath.NormalizeQuaternion(rotation);
        }

        var currentRotation = SceneTransformMath.NormalizeQuaternion(rotation);
        var stepDirection = Math.Sign(step.StepCount);
        var stepCount = Math.Abs(step.StepCount);
        for (var index = 0; index < stepCount; ++index)
        {
            if (!SceneSelectionTransformMath.TryResolveSurfaceDragRotationAxes(currentRotation, surfaceNormal, cameraRight, out var yawAxis, out var pitchAxis))
            {
                break;
            }

            currentRotation = SceneSelectionTransformMath.ApplySurfaceDragRotationStep(
                currentRotation,
                step.Axis == GizmoSurfaceDragRotationAxis.Yaw,
                stepDirection,
                GizmoConstants.SurfaceDragRotateStepDegrees,
                yawAxis,
                pitchAxis);
        }

        return currentRotation;
    }

    private static bool TryResolveSurfaceNormal(SceneSurfaceHit hit, out Vector3 surfaceNormal)
    {
        surfaceNormal = Vector3.UnitY;
        return NumericsUtility.HasLength(hit.Normal)
               && NumericsUtility.TryNormalize(hit.Normal, out surfaceNormal);
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
            var entryRotation = SceneTransformMath.NormalizeQuaternion(groupDelta * entry.StartRotationQuaternion);
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

