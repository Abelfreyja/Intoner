using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using System.Numerics;

namespace Intoner.Objects.Runtime;

internal sealed class SurfacePlacementService(
    FurnitureMetadataResolver metadataResolver,
    NativePlacementQuery nativeQuery,
    IObjectHousingModePolicy housingModePolicy)
{
    public bool TryResolvePlacementHit(
        ObjectSnapshot snapshot,
        ObjectBoundsSnapshot? boundsSnapshot,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        out ObjectSurfaceHit hit)
    {
        hit = ObjectSurfaceHit.Empty;
        if (!TryResolveHousingMetadata(snapshot, out HousingFurnitureMetadata metadata))
        {
            return false;
        }

        if (metadata.Surface == HousingPlacementSurface.Floor
            && boundsSnapshot is not null
            && PlacementSurfaceResolver.TryResolveNativePlacementClearance(boundsSnapshot, out ObjectPlacementClearance clearance)
            && nativeQuery.TryResolveFloorPlacementFromRay(rayOrigin, rayDirection, clearance.Radius, out hit))
        {
            return true;
        }

        return nativeQuery.TryRaycastMaterialMask(
            rayOrigin,
            rayDirection,
            PlacementValidationConstants.NativeRayMaxDistance,
            PlacementSurfacePolicy.ResolveAllowedMaterialMask(metadata),
            out hit);
    }

    public bool ShouldUseNativePlacementOrigin(ObjectSnapshot snapshot, ObjectSurfaceHit hit)
        => hit.Source == ObjectSurfaceHitSource.Native
           && TryResolveHousingMetadata(snapshot, out HousingFurnitureMetadata metadata)
           && metadata.Surface is HousingPlacementSurface.Floor or HousingPlacementSurface.Tabletop
           && PlacementSurfacePolicy.TryValidateSurface(metadata, hit, out _);

    public bool ShouldAlignWallSurface(ObjectSnapshot snapshot)
        => TryResolveHousingMetadata(snapshot, out HousingFurnitureMetadata metadata)
           && metadata.Surface == HousingPlacementSurface.Wall;

    private bool TryResolveHousingMetadata(ObjectSnapshot snapshot, out HousingFurnitureMetadata metadata)
    {
        if (!housingModePolicy.GetState().IsHousingMode)
        {
            metadata = default!;
            return false;
        }

        return metadataResolver.TryResolve(snapshot, out metadata);
    }
}

