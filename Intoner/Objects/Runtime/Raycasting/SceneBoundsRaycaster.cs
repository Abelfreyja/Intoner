using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Scene;

/// <summary> raycasts the shared bounds published by active scene items </summary>
internal static class SceneBoundsRaycaster
{
    public static bool TryRaycastNearest(
        IEnumerable<SceneItemBoundsSnapshot> boundsSnapshots,
        IReadOnlySet<Guid> excludedItemIds,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        out SceneSurfaceHit hit,
        float maxDistance)
        => TryRaycastNearestCore(
            boundsSnapshots,
            excludedItemIds,
            shouldSkipItem: null,
            acceptsHit: null,
            rayOrigin,
            rayDirection,
            out hit,
            maxDistance);

    internal static bool TryRaycastNearest<TBounds>(
        IEnumerable<TBounds> boundsSnapshots,
        Func<Guid, bool> shouldSkipItem,
        Func<TBounds, SceneSurfaceHit, bool> acceptsHit,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        out SceneSurfaceHit hit,
        float maxDistance)
        where TBounds : SceneItemBoundsSnapshot
        => TryRaycastNearestCore(
            boundsSnapshots,
            excludedItemIds: null,
            shouldSkipItem,
            acceptsHit,
            rayOrigin,
            rayDirection,
            out hit,
            maxDistance);

    private static bool TryRaycastNearestCore<TBounds>(
        IEnumerable<TBounds> boundsSnapshots,
        IReadOnlySet<Guid>? excludedItemIds,
        Func<Guid, bool>? shouldSkipItem,
        Func<TBounds, SceneSurfaceHit, bool>? acceptsHit,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        out SceneSurfaceHit hit,
        float maxDistance)
        where TBounds : SceneItemBoundsSnapshot
    {
        hit = SceneSurfaceHit.Empty;
        if (!NumericsUtility.TryNormalize(rayDirection, out Vector3 normalizedDirection))
        {
            return false;
        }

        float closestDistance = SceneRaycastMath.ResolveMaxDistance(maxDistance);
        bool hasHit = false;
        foreach (TBounds bounds in boundsSnapshots)
        {
            if (excludedItemIds?.Contains(bounds.Id) == true
                || shouldSkipItem?.Invoke(bounds.Id) == true
                || !TryRaycastBoundsNormalized(
                    rayOrigin,
                    normalizedDirection,
                    bounds,
                    out SceneSurfaceHit candidate,
                    closestDistance)
                || acceptsHit is not null && !acceptsHit(bounds, candidate))
            {
                continue;
            }

            closestDistance = candidate.Distance;
            hit = candidate with
            {
                Source = SceneSurfaceHitSource.ItemBounds,
                TargetItemId = bounds.Id,
            };
            hasHit = true;
        }

        return hasHit;
    }

    public static bool TryRaycastBounds(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        SceneItemBoundsSnapshot bounds,
        out SceneSurfaceHit hit,
        float maxDistance)
    {
        hit = SceneSurfaceHit.Empty;
        return NumericsUtility.TryNormalize(rayDirection, out Vector3 normalizedDirection)
            && TryRaycastBoundsNormalized(
                rayOrigin,
                normalizedDirection,
                bounds,
                out hit,
                SceneRaycastMath.ResolveMaxDistance(maxDistance));
    }

    internal static bool TryRaycastBoundsNormalized(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        SceneItemBoundsSnapshot bounds,
        out SceneSurfaceHit hit,
        float maxDistance)
        => bounds.OrientedBounds is { } orientedBounds
            ? TryRaycastOrientedBounds(rayOrigin, rayDirection, orientedBounds, out hit, maxDistance)
            : TryRaycastAxisAlignedBounds(rayOrigin, rayDirection, bounds.Min, bounds.Max, out hit, maxDistance);

    private static bool TryRaycastOrientedBounds(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        SceneOrientedBounds bounds,
        out SceneSurfaceHit hit,
        float maxDistance)
    {
        hit = SceneSurfaceHit.Empty;
        if (!Matrix4x4.Invert(bounds.Transform, out Matrix4x4 inverseTransform))
        {
            return false;
        }

        Vector3 localOrigin = Vector3.Transform(rayOrigin, inverseTransform);
        Vector3 localDirection = Vector3.TransformNormal(rayDirection, inverseTransform);
        if (!NumericsUtility.HasLength(localDirection))
        {
            return false;
        }

        Vector3 halfExtents = NumericsUtility.Abs(bounds.HalfExtents);
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

        Vector3 worldPoint = Vector3.Transform(localOrigin + (localDirection * localDistance), bounds.Transform);
        float worldDistance = Vector3.Dot(worldPoint - rayOrigin, rayDirection);
        if (worldDistance <= SceneRaycastMath.MinimumHitDistance || worldDistance >= maxDistance)
        {
            return false;
        }

        Matrix4x4 normalTransform = Matrix4x4.Transpose(inverseTransform);
        Vector3 worldNormal = Vector3.TransformNormal(localNormal, normalTransform);
        Vector3 orientedNormal = SceneRaycastMath.OrientSurfaceNormal(worldNormal, rayDirection);
        if (!NumericsUtility.HasLength(orientedNormal))
        {
            return false;
        }

        hit = new SceneSurfaceHit(worldPoint, orientedNormal, Distance: worldDistance);
        return true;
    }

    private static bool TryRaycastAxisAlignedBounds(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        Vector3 min,
        Vector3 max,
        out SceneSurfaceHit hit,
        float maxDistance)
    {
        hit = SceneSurfaceHit.Empty;
        Vector3 orderedMin = Vector3.Min(min, max);
        Vector3 orderedMax = Vector3.Max(min, max);
        if (!TryRaycastBox(rayOrigin, rayDirection, orderedMin, orderedMax, out float distance, out Vector3 normal)
            || distance <= SceneRaycastMath.MinimumHitDistance
            || distance >= maxDistance)
        {
            return false;
        }

        Vector3 orientedNormal = SceneRaycastMath.OrientSurfaceNormal(normal, rayDirection);
        if (!NumericsUtility.HasLength(orientedNormal))
        {
            return false;
        }

        hit = new SceneSurfaceHit(rayOrigin + (rayDirection * distance), orientedNormal, Distance: distance);
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
        float enterDistance = float.NegativeInfinity;
        float exitDistance = float.PositiveInfinity;
        Vector3 enterNormal = Vector3.Zero;
        Vector3 exitNormal = Vector3.Zero;

        if (!ClipSlab(rayOrigin.X, rayDirection.X, min.X, max.X, -Vector3.UnitX, Vector3.UnitX, ref enterDistance, ref exitDistance, ref enterNormal, ref exitNormal)
            || !ClipSlab(rayOrigin.Y, rayDirection.Y, min.Y, max.Y, -Vector3.UnitY, Vector3.UnitY, ref enterDistance, ref exitDistance, ref enterNormal, ref exitNormal)
            || !ClipSlab(rayOrigin.Z, rayDirection.Z, min.Z, max.Z, -Vector3.UnitZ, Vector3.UnitZ, ref enterDistance, ref exitDistance, ref enterNormal, ref exitNormal)
            || exitDistance <= SceneRaycastMath.MinimumHitDistance)
        {
            return false;
        }

        bool startsInside = enterDistance <= SceneRaycastMath.MinimumHitDistance;
        distance = startsInside ? exitDistance : enterDistance;
        normal = startsInside ? exitNormal : enterNormal;
        return NumericsUtility.HasLength(normal);
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
        ref Vector3 enterNormal,
        ref Vector3 exitNormal)
    {
        if (NumericsUtility.IsNearlyZero(direction))
        {
            return origin >= min && origin <= max;
        }

        float nearDistance = (min - origin) / direction;
        float farDistance = (max - origin) / direction;
        Vector3 nearNormal = minNormal;
        Vector3 farNormal = maxNormal;
        if (nearDistance > farDistance)
        {
            (nearDistance, farDistance) = (farDistance, nearDistance);
            (nearNormal, farNormal) = (farNormal, nearNormal);
        }

        if (nearDistance > enterDistance)
        {
            enterDistance = nearDistance;
            enterNormal = nearNormal;
        }

        if (farDistance < exitDistance)
        {
            exitDistance = farDistance;
            exitNormal = farNormal;
        }

        return enterDistance <= exitDistance;
    }
}
