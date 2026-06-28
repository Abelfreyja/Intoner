using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.Runtime;

internal readonly record struct PlacementSceneRaycastRequest(
    Guid ObjectId,
    Vector3 Origin,
    Vector3 Direction,
    float MaxDistance,
    ulong NativeMaterialMask = 0);

internal sealed class PlacementSceneRaycaster(NativePlacementQuery nativeQuery)
{
    public bool TryRaycastNative(
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        out ObjectSurfaceHit hit)
        => nativeQuery.TryRaycast(origin, direction, maxDistance, out hit);

    public bool TryRaycastNativeMaterial(
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        ulong materialMask,
        out ObjectSurfaceHit hit)
        => nativeQuery.TryRaycastMaterialMask(origin, direction, maxDistance, materialMask, out hit);

    public bool TryRaycastObjectBounds(
        PlacementValidationContext context,
        PlacementSceneRaycastRequest request,
        out ObjectSurfaceHit hit)
        => TryRaycastObjectBounds(context, request, requirePlacementSurface: true, out hit);

    public bool TryRaycastAny(
        PlacementValidationContext context,
        PlacementSceneRaycastRequest request,
        out ObjectSurfaceHit hit)
    {
        hit = new ObjectSurfaceHit(Vector3.Zero, Vector3.Zero);
        float closestDistance = ObjectSurfaceRaycastUtility.ResolveMaxDistance(request.MaxDistance);
        bool hasHit = false;

        if (nativeQuery.TryRaycast(request.Origin, request.Direction, closestDistance, out ObjectSurfaceHit nativeHit))
        {
            closestDistance = nativeHit.Distance;
            hit = nativeHit;
            hasHit = true;
        }

        if (request.NativeMaterialMask != 0
            && TryRaycastNativeMaterial(request.Origin, request.Direction, closestDistance, request.NativeMaterialMask, out ObjectSurfaceHit filteredHit))
        {
            closestDistance = filteredHit.Distance;
            hit = filteredHit;
            hasHit = true;
        }

        if (TryRaycastObjectBounds(
                context,
                request with { MaxDistance = closestDistance },
                request.NativeMaterialMask != 0,
                out ObjectSurfaceHit objectHit))
        {
            hit = objectHit;
            hasHit = true;
        }

        return hasHit;
    }

    private static bool TryRaycastObjectBounds(
        PlacementValidationContext context,
        PlacementSceneRaycastRequest request,
        bool requirePlacementSurface,
        out ObjectSurfaceHit hit)
    {
        Func<ObjectBoundsSnapshot, ObjectSurfaceHit, bool> acceptsHit = requirePlacementSurface
            ? HasSupportedObjectSurface
            : static (_, _) => true;
        if (!ObjectPlacementBoundsRaycaster.TryRaycastNearest(
                context.BoundsById.Values,
                targetObjectId => !CanUseObjectBoundsTarget(context, request.ObjectId, targetObjectId),
                acceptsHit,
                request.Origin,
                request.Direction,
                out hit,
                request.MaxDistance))
        {
            return false;
        }

        if (!requirePlacementSurface)
        {
            return true;
        }

        if (!TryResolveObjectSurfaceMaterial(context, hit, out ulong material))
        {
            return false;
        }

        hit = hit with { Material = material };
        return true;
    }

    private static bool HasSupportedObjectSurface(ObjectBoundsSnapshot boundsSnapshot, ObjectSurfaceHit hit)
        => PlacementSurfacePolicy.ResolveObjectSurfaceMaterial(hit.Normal, boundsSnapshot.PlacementSurfaceSupport) != 0;

    private static bool TryResolveObjectSurfaceMaterial(
        PlacementValidationContext context,
        ObjectSurfaceHit hit,
        out ulong material)
    {
        material = 0;
        if (!context.BoundsById.TryGetValue(hit.TargetObjectId, out ObjectBoundsSnapshot? targetBounds))
        {
            return false;
        }

        material = PlacementSurfacePolicy.ResolveObjectSurfaceMaterial(hit.Normal, targetBounds.PlacementSurfaceSupport);
        return material != 0;
    }

    private static bool CanUseObjectBoundsTarget(
        PlacementValidationContext context,
        Guid objectId,
        Guid targetObjectId)
        => targetObjectId != objectId
           && context.SnapshotsById.TryGetValue(targetObjectId, out ObjectSnapshot? targetSnapshot)
           && targetSnapshot is { Kind: ObjectKind.Furniture };
}

