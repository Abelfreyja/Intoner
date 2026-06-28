using Intoner.Objects.Models;
using Intoner.Objects.Rendering.Drawing;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class Gizmo
{
    private static readonly (GizmoAxis Primary, GizmoAxis Secondary)[] GizmoSnapGridPlaneCandidates =
    [
        (GizmoAxis.X, GizmoAxis.Y),
        (GizmoAxis.X, GizmoAxis.Z),
        (GizmoAxis.Y, GizmoAxis.Z),
    ];

    private void DrawTranslationSnapGrid(in GizmoContext context, float scale, GizmoAxis preferredAxis)
    {
        var snapPolicy = ResolveActiveTransformSnapPolicy(context);
        if (!snapPolicy.PositionEnabled || !ObjectMathUtility.HasLength(snapPolicy.PositionStep))
        {
            return;
        }

        var snapBasis = snapPolicy.Position.Basis;
        var preferredPlaneNormal = ResolvePreferredTranslationSnapGridPlaneNormal(context);
        if (!TryResolveTranslationSnapGridAxes(context, snapBasis, preferredPlaneNormal, preferredAxis, out var primaryAxis, out var secondaryAxis))
        {
            return;
        }

        var primaryDirection = ResolveSnapGridAxisDirection(primaryAxis, snapBasis);
        var secondaryDirection = ResolveSnapGridAxisDirection(secondaryAxis, snapBasis);
        if (!ObjectMathUtility.HasLength(primaryDirection) || !ObjectMathUtility.HasLength(secondaryDirection))
        {
            return;
        }

        var requestedExtent = MathF.Max(context.AxisWorldLength * 1.25f, snapPolicy.PositionStep * 3f);
        var cellsPerSide = Math.Clamp((int)MathF.Ceiling(requestedExtent / snapPolicy.PositionStep), 2, GizmoConstants.SnapGridMaxCellsPerSide);
        var halfExtent = cellsPerSide * snapPolicy.PositionStep;
        var primaryColor = GetAxisColorVector(primaryAxis, false, true);
        var secondaryColor = GetAxisColorVector(secondaryAxis, false, true);
        var gridOrigin = snapPolicy.ResolveGridOrigin(primaryAxis, secondaryAxis, preferredAxis);

        var batch = _drawManager.BeginPass(DrawPassKind.GizmoSnapGrid, "Gizmo Snap Grid", DrawLayer.Foreground);
        for (var index = -cellsPerSide; index <= cellsPerSide; ++index)
        {
            var offset = index * snapPolicy.PositionStep;
            var isCenterLine = index == 0;
            var lineAlpha = isCenterLine ? GizmoConstants.SnapGridCenterLineAlpha : GizmoConstants.SnapGridLineAlpha;
            var primaryLineColor = primaryColor with { W = lineAlpha };
            var secondaryLineColor = secondaryColor with { W = lineAlpha };
            var thickness = (isCenterLine ? GizmoConstants.SnapGridCenterLineThickness : GizmoConstants.SnapGridLineThickness) * scale;

            batch.AddLine(
                gridOrigin + (secondaryDirection * offset) - (primaryDirection * halfExtent),
                gridOrigin + (secondaryDirection * offset) + (primaryDirection * halfExtent),
                primaryLineColor,
                thickness);

            batch.AddLine(
                gridOrigin + (primaryDirection * offset) - (secondaryDirection * halfExtent),
                gridOrigin + (primaryDirection * offset) + (secondaryDirection * halfExtent),
                secondaryLineColor,
                thickness);
        }
    }

    private static bool TryResolveTranslationSnapGridAxes(
        in GizmoContext context,
        in ObjectSnapBasis basis,
        Vector3? preferredPlaneNormal,
        GizmoAxis preferredAxis,
        out GizmoAxis primaryAxis,
        out GizmoAxis secondaryAxis)
    {
        primaryAxis = GizmoAxis.None;
        secondaryAxis = GizmoAxis.None;

        if (preferredAxis != GizmoAxis.None)
        {
            primaryAxis = preferredAxis;
            secondaryAxis = ResolveBestCompanionGridAxis(context, basis, preferredPlaneNormal, preferredAxis);
            return secondaryAxis != GizmoAxis.None;
        }

        var bestScore = float.MinValue;

        foreach (var candidate in GizmoSnapGridPlaneCandidates)
        {
            var score = ResolveTranslationSnapGridPlaneScore(context, basis, preferredPlaneNormal, candidate.Primary, candidate.Secondary);
            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            primaryAxis = candidate.Primary;
            secondaryAxis = candidate.Secondary;
        }

        return primaryAxis != GizmoAxis.None && secondaryAxis != GizmoAxis.None;
    }

    private static GizmoAxis ResolveBestCompanionGridAxis(
        in GizmoContext context,
        in ObjectSnapBasis basis,
        Vector3? preferredPlaneNormal,
        GizmoAxis primaryAxis)
    {
        GizmoAxis bestAxis = GizmoAxis.None;
        var bestScore = float.MinValue;

        for (var index = 0; index < GizmoAxisUtility.AxisCount; ++index)
        {
            var candidateAxis = GizmoAxisUtility.FromIndex(index);
            if (candidateAxis == primaryAxis)
            {
                continue;
            }

            var score = ResolveTranslationSnapGridPlaneScore(context, basis, preferredPlaneNormal, primaryAxis, candidateAxis);
            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestAxis = candidateAxis;
        }

        return bestAxis;
    }

    private static float ResolveTranslationSnapGridPlaneScore(
        in GizmoContext context,
        in ObjectSnapBasis basis,
        Vector3? preferredPlaneNormal,
        GizmoAxis primaryAxis,
        GizmoAxis secondaryAxis)
    {
        var primaryDirection = ResolveSnapGridAxisDirection(primaryAxis, basis);
        var secondaryDirection = ResolveSnapGridAxisDirection(secondaryAxis, basis);
        var planeNormal = Vector3.Cross(primaryDirection, secondaryDirection);
        if (!ObjectMathUtility.TryNormalize(planeNormal, out var normalizedPlaneNormal))
        {
            return float.MinValue;
        }

        var score = 0f;
        if (preferredPlaneNormal is { } surfaceNormal
            && ObjectMathUtility.TryNormalize(surfaceNormal, out var normalizedSurfaceNormal))
        {
            score += MathF.Abs(Vector3.Dot(normalizedPlaneNormal, normalizedSurfaceNormal)) * 10f;
        }

        if (!context.CameraViewDirection.HasValue || !ObjectMathUtility.TryNormalize(context.CameraViewDirection.Value, out var normalizedCameraDirection))
        {
            return score;
        }

        return score + MathF.Abs(Vector3.Dot(normalizedPlaneNormal, normalizedCameraDirection));
    }

    private Vector3? ResolvePreferredTranslationSnapGridPlaneNormal(in GizmoContext context)
    {
        if (!SurfaceDragState.Matches(context.PrimarySnapshot.Id)
            || !TryResolveCurrentPlacementHit(context, out var hit)
            || !ObjectMathUtility.TryNormalize(hit.Normal, out var surfaceNormal))
        {
            return null;
        }

        return surfaceNormal;
    }

    private static Vector3 ResolveSnapGridAxisDirection(GizmoAxis axis, in ObjectSnapBasis basis)
        => ResolveAxisWorldDirection(axis, basis.Rotation, basis.Rotation == Quaternion.Identity);
}

