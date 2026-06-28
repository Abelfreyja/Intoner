using System.Numerics;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;

namespace Intoner.Objects.Utils;

internal static class ObjectBoundsRaycastUtility
{
    public static bool TryRaycastOrientedBounds(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        OrientedBounds bounds,
        out ObjectSurfaceHit hit,
        float maxDistance)
    {
        hit = new ObjectSurfaceHit(Vector3.Zero, Vector3.Zero);
        if (!ObjectMathUtility.TryNormalize(rayDirection, out var normalizedDirection)
            || !Matrix4x4.Invert(bounds.Transform, out var inverseTransform))
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
        if (worldDistance <= ObjectSurfaceRaycastUtility.MinimumHitDistance || worldDistance >= maxDistance)
        {
            return false;
        }

        Vector3 worldNormal = Vector3.TransformNormal(localNormal, bounds.Transform);
        Vector3 orientedNormal = ObjectSurfaceRaycastUtility.OrientSurfaceNormal(worldNormal, normalizedDirection);
        if (!ObjectMathUtility.HasLength(orientedNormal))
        {
            return false;
        }

        hit = new ObjectSurfaceHit(worldPoint, orientedNormal, Distance: worldDistance);
        return true;
    }

    public static bool TryRaycastAxisAlignedBounds(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        Vector3 min,
        Vector3 max,
        out ObjectSurfaceHit hit,
        float maxDistance)
    {
        hit = new ObjectSurfaceHit(Vector3.Zero, Vector3.Zero);
        if (!ObjectMathUtility.TryNormalize(rayDirection, out var normalizedDirection)
            || !TryRaycastBox(rayOrigin, normalizedDirection, min, max, out float distance, out Vector3 normal)
            || distance <= ObjectSurfaceRaycastUtility.MinimumHitDistance
            || distance >= maxDistance)
        {
            return false;
        }

        Vector3 orientedNormal = ObjectSurfaceRaycastUtility.OrientSurfaceNormal(normal, normalizedDirection);
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
        var exitDistance = float.PositiveInfinity;

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
        return enterDistance <= exitDistance && exitDistance > ObjectSurfaceRaycastUtility.MinimumHitDistance;
    }
}

