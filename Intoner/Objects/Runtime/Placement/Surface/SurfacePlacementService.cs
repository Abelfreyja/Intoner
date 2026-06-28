using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
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
        hit = new ObjectSurfaceHit(Vector3.Zero, Vector3.Zero);
        if (!housingModePolicy.GetState().IsHousingMode
            || boundsSnapshot is null
            || !metadataResolver.TryResolve(snapshot, out HousingFurnitureMetadata metadata)
            || metadata is not { Surface: HousingPlacementSurface.Floor }
            || !PlacementSurfaceResolver.TryResolveNativePlacementClearanceRadius(boundsSnapshot, out float radius))
        {
            return false;
        }

        return nativeQuery.TryResolveFloorPlacementFromRay(rayOrigin, rayDirection, radius, out hit);
    }

    public bool ShouldUseNativePlacementOrigin(ObjectSnapshot snapshot, ObjectSurfaceHit hit)
    {
        if (!housingModePolicy.GetState().IsHousingMode
            || hit.Source != ObjectSurfaceHitSource.Native
            || !metadataResolver.TryResolve(snapshot, out HousingFurnitureMetadata metadata))
        {
            return false;
        }

        return metadata is { Surface: HousingPlacementSurface.Floor or HousingPlacementSurface.Tabletop };
    }
}

