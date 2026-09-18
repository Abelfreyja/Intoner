using Intoner.Objects.Models;
using Intoner.Scene;

namespace Intoner.Objects.Runtime;

internal sealed class PlacementSurfaceRaycaster(NativePlacementQuery nativeQuery)
{
    public bool TryRaycastNative(PlacementSurfaceRaycastRequest request, out SceneSurfaceHit hit)
        => nativeQuery.TryRaycast(request, out hit);

    public static bool TryRaycastObjectBounds(
        PlacementValidationContext context,
        PlacementSurfaceRaycastRequest request,
        out SceneSurfaceHit hit)
        => TryRaycastObjectBounds(context, request, requirePlacementSurface: true, out hit);

    public bool TryRaycastAny(
        PlacementValidationContext context,
        PlacementSurfaceRaycastRequest request,
        out SceneSurfaceHit hit)
    {
        hit = SceneSurfaceHit.Empty;
        float closestDistance = SceneRaycastMath.ResolveMaxDistance(request.MaxDistance);
        bool hasHit = false;

        if (request.NativeMaterialMask != 0
            && TryRaycastNative(request with { MaxDistance = closestDistance }, out SceneSurfaceHit filteredHit))
        {
            closestDistance = filteredHit.Distance;
            hit = filteredHit;
            hasHit = true;
        }
        else if (TryRaycastNative(request with { MaxDistance = closestDistance, NativeMaterialMask = 0 }, out SceneSurfaceHit nativeHit))
        {
            closestDistance = nativeHit.Distance;
            hit = nativeHit;
            hasHit = true;
        }

        if (TryRaycastObjectBounds(
                context,
                request with { MaxDistance = closestDistance },
                request.NativeMaterialMask != 0,
                out SceneSurfaceHit objectHit))
        {
            hit = objectHit;
            hasHit = true;
        }

        return hasHit;
    }

    private static bool TryRaycastObjectBounds(
        PlacementValidationContext context,
        PlacementSurfaceRaycastRequest request,
        bool requirePlacementSurface,
        out SceneSurfaceHit hit)
    {
        Func<ObjectBoundsSnapshot, SceneSurfaceHit, bool> acceptsHit = requirePlacementSurface
            ? HasSupportedObjectSurface
            : static (_, _) => true;
        if (!SceneBoundsRaycaster.TryRaycastNearest(
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

    private static bool HasSupportedObjectSurface(ObjectBoundsSnapshot boundsSnapshot, SceneSurfaceHit hit)
        => PlacementSurfacePolicy.ResolveObjectSurfaceMaterial(hit.Normal, boundsSnapshot.PlacementSurfaceSupport) != 0;

    private static bool TryResolveObjectSurfaceMaterial(
        PlacementValidationContext context,
        SceneSurfaceHit hit,
        out ulong material)
    {
        material = 0;
        if (!context.BoundsById.TryGetValue(hit.TargetItemId, out ObjectBoundsSnapshot? targetBounds))
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
