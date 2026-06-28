using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.Runtime;

internal sealed class FootprintRule(PlacementEvaluationFactory evaluationFactory) : IPlacementRule
{
    private const float PileLimitScale = 0.01f;
    private const float PileLimitBroadphaseScale = 1.1f;

    public bool TryEvaluate(
        PlacementValidationContext context,
        ObjectSnapshot snapshot,
        ObjectBoundsSnapshot boundsSnapshot,
        out PlacementEvaluation evaluation)
    {
        if (!context.TryGetMetadata(snapshot.Id, out HousingFurnitureMetadata metadata))
        {
            evaluation = default!;
            return false;
        }

        if (metadata.RequiresAquariumFootprintValidation && !metadata.HasAquariumFootprint)
        {
            evaluation = evaluationFactory.Unknown(
                snapshot.Id,
                PlacementIssueCode.MissingAquariumFootprint,
                "Aquarium and showcase footprint metadata is not available.");
            return true;
        }

        if (metadata.HasAquariumFootprint
            && HasPileFootprintConflict(snapshot, metadata, context))
        {
            evaluation = evaluationFactory.Invalid(
                snapshot,
                PlacementIssueCode.FootprintConflict,
                "Aquarium or showcase footprint overlaps another tabletop display item.");
            return true;
        }

        evaluation = default!;
        return false;
    }

    private static bool HasPileFootprintConflict(
        ObjectSnapshot snapshot,
        HousingFurnitureMetadata metadata,
        PlacementValidationContext context)
    {
        if (metadata.PileFootprint is not { HasArea: true } footprint)
        {
            return false;
        }

        foreach (ObjectSnapshot otherSnapshot in context.Snapshots)
        {
            if (otherSnapshot.Id == snapshot.Id
                || !context.TryGetMetadata(otherSnapshot.Id, out HousingFurnitureMetadata otherMetadata)
                || otherMetadata.PileFootprint is not { HasArea: true } otherFootprint)
            {
                continue;
            }

            if (otherFootprint.AllowsOverlapWith(metadata.AquariumTier))
            {
                continue;
            }

            if (PileFootprintsOverlap(snapshot, footprint, otherSnapshot, otherFootprint))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PileFootprintsOverlap(
        ObjectSnapshot left,
        HousingPileFootprint leftFootprint,
        ObjectSnapshot right,
        HousingPileFootprint rightFootprint)
    {
        if (!PileHeightsOverlap(left.Transform.Position.Y, leftFootprint, right.Transform.Position.Y, rightFootprint))
        {
            return false;
        }

        PileFootprintRect leftRect = BuildPileFootprintRect(left, leftFootprint);
        PileFootprintRect rightRect = BuildPileFootprintRect(right, rightFootprint);
        float broadphaseRadius = (leftRect.Radius + rightRect.Radius) * PileLimitBroadphaseScale;
        if (Vector2.DistanceSquared(leftRect.Center, rightRect.Center) > broadphaseRadius * broadphaseRadius)
        {
            return false;
        }

        return PileRectanglesOverlap(leftRect, rightRect);
    }

    private static bool PileHeightsOverlap(
        float leftY,
        HousingPileFootprint leftFootprint,
        float rightY,
        HousingPileFootprint rightFootprint)
    {
        if (rightFootprint.Height != 0 && leftY > rightY + (rightFootprint.Height * PileLimitScale))
        {
            return false;
        }

        return leftFootprint.Height == 0 || rightY <= leftY + (leftFootprint.Height * PileLimitScale);
    }

    private static PileFootprintRect BuildPileFootprintRect(ObjectSnapshot snapshot, HousingPileFootprint footprint)
    {
        Quaternion rotation = ObjectTransformMath.CreateRotationQuaternion(snapshot.Transform.RotationDegrees);
        Vector2 right = ResolveFootprintAxis(Vector3.Transform(Vector3.UnitX, rotation), Vector2.UnitX);
        Vector2 forward = ResolveFootprintAxis(Vector3.Transform(Vector3.UnitZ, rotation), Vector2.UnitY);
        Vector2 halfExtents = new(
            footprint.Width * PileLimitScale * 0.5f,
            footprint.Depth * PileLimitScale * 0.5f);

        return new PileFootprintRect(
            new Vector2(snapshot.Transform.Position.X, snapshot.Transform.Position.Z),
            right,
            forward,
            halfExtents);
    }

    private static Vector2 ResolveFootprintAxis(Vector3 axis, Vector2 fallback)
    {
        Vector2 projectedAxis = new(axis.X, axis.Z);
        return ObjectMathUtility.TryNormalize(projectedAxis, out Vector2 normalizedAxis)
            ? normalizedAxis
            : fallback;
    }

    private static bool PileRectanglesOverlap(PileFootprintRect left, PileFootprintRect right)
        => OverlapsOnAxis(left, right, left.Right)
           && OverlapsOnAxis(left, right, left.Forward)
           && OverlapsOnAxis(left, right, right.Right)
           && OverlapsOnAxis(left, right, right.Forward);

    private static bool OverlapsOnAxis(PileFootprintRect left, PileFootprintRect right, Vector2 axis)
    {
        float leftRadius = left.ProjectRadius(axis);
        float rightRadius = right.ProjectRadius(axis);
        float distance = MathF.Abs(Vector2.Dot(right.Center - left.Center, axis));
        return distance <= leftRadius + rightRadius;
    }

    private readonly record struct PileFootprintRect(
        Vector2 Center,
        Vector2 Right,
        Vector2 Forward,
        Vector2 HalfExtents)
    {
        public float Radius
            => MathF.Max(HalfExtents.X, HalfExtents.Y);

        public float ProjectRadius(Vector2 axis)
            => (MathF.Abs(Vector2.Dot(Right, axis)) * HalfExtents.X)
               + (MathF.Abs(Vector2.Dot(Forward, axis)) * HalfExtents.Y);
    }
}

