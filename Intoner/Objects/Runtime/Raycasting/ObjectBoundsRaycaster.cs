using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;

namespace Intoner.Objects.Runtime;

internal static class ObjectBoundsRaycaster
{
    public static bool TryRaycastNearest(
        IEnumerable<ObjectBoundsSnapshot> boundsSnapshots,
        Func<Guid, bool> shouldSkipObject,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        out ObjectSurfaceHit hit,
        float maxDistance)
        => TryRaycastNearest(
            boundsSnapshots,
            shouldSkipObject,
            static (_, _) => true,
            rayOrigin,
            rayDirection,
            out hit,
            maxDistance);

    public static bool TryRaycastNearest(
        IEnumerable<ObjectBoundsSnapshot> boundsSnapshots,
        Func<Guid, bool> shouldSkipObject,
        Func<ObjectBoundsSnapshot, ObjectSurfaceHit, bool> acceptsHit,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        out ObjectSurfaceHit hit,
        float maxDistance)
    {
        hit = ObjectSurfaceHit.Empty;
        if (!ObjectMathUtility.TryNormalize(rayDirection, out Vector3 normalizedDirection))
        {
            return false;
        }

        float closestDistance = ObjectRaycastMath.ResolveMaxDistance(maxDistance);
        bool hasHit = false;
        foreach (ObjectBoundsSnapshot boundsSnapshot in boundsSnapshots)
        {
            if (shouldSkipObject(boundsSnapshot.Id)
                || !TryRaycastBounds(rayOrigin, normalizedDirection, boundsSnapshot, out ObjectSurfaceHit candidate, closestDistance)
                || !acceptsHit(boundsSnapshot, candidate))
            {
                continue;
            }

            closestDistance = candidate.Distance;
            hit = candidate with
            {
                Source = ObjectSurfaceHitSource.ObjectBounds,
                TargetObjectId = boundsSnapshot.Id,
            };
            hasHit = true;
        }

        return hasHit;
    }

    public static bool TryRaycastBounds(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        ObjectBoundsSnapshot boundsSnapshot,
        out ObjectSurfaceHit hit,
        float maxDistance)
        => boundsSnapshot.LocalBounds is { } localBounds
            ? TryRaycastOrientedBounds(rayOrigin, rayDirection, localBounds, out hit, maxDistance)
            : TryRaycastAxisAlignedBounds(rayOrigin, rayDirection, boundsSnapshot.Min, boundsSnapshot.Max, out hit, maxDistance);

    private static bool TryRaycastOrientedBounds(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        OrientedBounds bounds,
        out ObjectSurfaceHit hit,
        float maxDistance)
    {
        hit = ObjectSurfaceHit.Empty;
        if (!ObjectMathUtility.TryNormalize(rayDirection, out Vector3 normalizedDirection)
            || !Matrix4x4.Invert(bounds.Transform, out Matrix4x4 inverseTransform))
        {
            return false;
        }

        Vector3 localOrigin = Vector3.Transform(rayOrigin, inverseTransform);
        Vector3 localDirection = Vector3.TransformNormal(normalizedDirection, inverseTransform);
        if (!ObjectMathUtility.HasLength(localDirection))
        {
            return false;
        }

        Vector3 halfExtents = ObjectMathUtility.Abs(bounds.HalfExtents);
        if (!TryRaycastBox(
                localOrigin,
                localDirection,
                -halfExtents,
                halfExtents,
                out float localDistance,
                out Vector3 localNormal))
        {
            return false;
        }

        Vector3 localPoint = localOrigin + (localDirection * localDistance);
        Vector3 worldPoint = Vector3.Transform(localPoint, bounds.Transform);
        float worldDistance = Vector3.Dot(worldPoint - rayOrigin, normalizedDirection);
        if (worldDistance <= ObjectRaycastMath.MinimumHitDistance || worldDistance >= maxDistance)
        {
            return false;
        }

        Vector3 worldNormal = Vector3.TransformNormal(localNormal, bounds.Transform);
        Vector3 orientedNormal = ObjectRaycastMath.OrientSurfaceNormal(worldNormal, normalizedDirection);
        if (!ObjectMathUtility.HasLength(orientedNormal))
        {
            return false;
        }

        hit = new ObjectSurfaceHit(worldPoint, orientedNormal, Distance: worldDistance);
        return true;
    }

    private static bool TryRaycastAxisAlignedBounds(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        Vector3 min,
        Vector3 max,
        out ObjectSurfaceHit hit,
        float maxDistance)
    {
        hit = ObjectSurfaceHit.Empty;
        if (!ObjectMathUtility.TryNormalize(rayDirection, out Vector3 normalizedDirection)
            || !TryRaycastBox(rayOrigin, normalizedDirection, min, max, out float distance, out Vector3 normal)
            || distance <= ObjectRaycastMath.MinimumHitDistance
            || distance >= maxDistance)
        {
            return false;
        }

        Vector3 orientedNormal = ObjectRaycastMath.OrientSurfaceNormal(normal, normalizedDirection);
        if (!ObjectMathUtility.HasLength(orientedNormal))
        {
            return false;
        }

        hit = new ObjectSurfaceHit(rayOrigin + (normalizedDirection * distance), orientedNormal, Distance: distance);
        return true;
    }

    private static bool TryRaycastBox(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        Vector3 min,
        Vector3 max,
        out float distance,
        out Vector3 normal)
    {
        distance = 0f;
        normal = Vector3.Zero;
        float exitDistance = float.PositiveInfinity;

        return ClipSlab(rayOrigin.X, rayDirection.X, min.X, max.X, -Vector3.UnitX, Vector3.UnitX, ref distance, ref exitDistance, ref normal)
            && ClipSlab(rayOrigin.Y, rayDirection.Y, min.Y, max.Y, -Vector3.UnitY, Vector3.UnitY, ref distance, ref exitDistance, ref normal)
            && ClipSlab(rayOrigin.Z, rayDirection.Z, min.Z, max.Z, -Vector3.UnitZ, Vector3.UnitZ, ref distance, ref exitDistance, ref normal)
            && ObjectMathUtility.HasLength(normal);
    }

    private static bool ClipSlab(
        float origin,
        float direction,
        float min,
        float max,
        Vector3 minNormal,
        Vector3 maxNormal,
        ref float enterDistance,
        ref float exitDistance,
        ref Vector3 enterNormal)
    {
        if (ObjectMathUtility.IsNearlyZero(direction))
        {
            return origin >= min && origin <= max;
        }

        float nearDistance = (min - origin) / direction;
        float farDistance = (max - origin) / direction;
        Vector3 nearNormal = minNormal;
        if (nearDistance > farDistance)
        {
            (nearDistance, farDistance) = (farDistance, nearDistance);
            nearNormal = maxNormal;
        }

        if (nearDistance > enterDistance)
        {
            enterDistance = nearDistance;
            enterNormal = nearNormal;
        }

        exitDistance = MathF.Min(exitDistance, farDistance);
        return enterDistance <= exitDistance && exitDistance > ObjectRaycastMath.MinimumHitDistance;
    }
}
