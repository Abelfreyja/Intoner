using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.Runtime;

internal static class ObjectPlacementBoundsRaycaster
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
        hit = new ObjectSurfaceHit(Vector3.Zero, Vector3.Zero);
        if (!ObjectMathUtility.TryNormalize(rayDirection, out Vector3 normalizedDirection))
        {
            return false;
        }

        float closestDistance = ObjectSurfaceRaycastUtility.ResolveMaxDistance(maxDistance);
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
            ? ObjectBoundsRaycastUtility.TryRaycastOrientedBounds(rayOrigin, rayDirection, localBounds, out hit, maxDistance)
            : ObjectBoundsRaycastUtility.TryRaycastAxisAlignedBounds(rayOrigin, rayDirection, boundsSnapshot.Min, boundsSnapshot.Max, out hit, maxDistance);
}

