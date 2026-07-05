using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using System.Numerics;

namespace Intoner.Objects.Runtime;

internal static class PlacementSurfacePolicy
{
    public const ulong FloorMaterial = 0x10000;
    public const ulong TabletopMaterial = 0x80000;
    public const ulong WallMaterial = 0x100000;

    private const float TabletopNormalThreshold = 0.5f;

    public static bool TryValidateSurface(
        HousingFurnitureMetadata metadata,
        ObjectSurfaceHit hit,
        out string errorMessage)
    {
        if (AllowsSurface(metadata, hit))
        {
            errorMessage = string.Empty;
            return true;
        }

        if (hit.Material == 0)
        {
            errorMessage = "Housing mode requires a collision material hit.";
            return false;
        }

        errorMessage = metadata.Surface switch
        {
            HousingPlacementSurface.Wall     => "Wall-mounted furniture requires a wall placement surface.",
            HousingPlacementSurface.Tabletop => "Tabletop furniture requires a floor or tabletop placement surface.",
            _                                => "Floor furniture requires a floor placement surface.",
        };
        return false;
    }

    public static bool AllowsSurface(HousingFurnitureMetadata metadata, ObjectSurfaceHit hit)
        => metadata.Surface == HousingPlacementSurface.Floor
            ? AllowsFloorSurface(hit)
            : hit.HasMaterial(ResolveAllowedMaterialMask(metadata));

    public static ulong ResolveAllowedMaterialMask(HousingFurnitureMetadata metadata)
        => metadata.Surface switch
        {
            HousingPlacementSurface.Wall     => WallMaterial,
            HousingPlacementSurface.Tabletop => FloorMaterial | TabletopMaterial,
            _                                => FloorMaterial | TabletopMaterial,
        };

    public static bool SupportsObjectSurface(
        ObjectPlacementSurfaceSupport support,
        HousingPlacementSurface surface)
        => surface switch
        {
            HousingPlacementSurface.Tabletop => HasSurfaceSupport(support, ObjectPlacementSurfaceSupport.Tabletop),
            HousingPlacementSurface.Wall     => HasSurfaceSupport(support, ObjectPlacementSurfaceSupport.Wall),
            _                                => false,
        };

    public static ulong ResolveObjectSurfaceMaterial(Vector3 normal, ObjectPlacementSurfaceSupport support)
    {
        if (normal.Y >= TabletopNormalThreshold
            && HasSurfaceSupport(support, ObjectPlacementSurfaceSupport.Tabletop))
        {
            return TabletopMaterial;
        }

        return WallPlacementGeometry.IsWallSurfaceNormal(normal)
            && HasSurfaceSupport(support, ObjectPlacementSurfaceSupport.Wall)
            ? WallMaterial
            : 0;
    }

    private static bool AllowsFloorSurface(ObjectSurfaceHit hit)
    {
        if (hit.HasMaterial(FloorMaterial))
        {
            return true;
        }

        return hit.Source == ObjectSurfaceHitSource.Native
            && hit.Normal.Y >= TabletopNormalThreshold;
    }

    private static bool HasSurfaceSupport(ObjectPlacementSurfaceSupport support, ObjectPlacementSurfaceSupport required)
        => (support & required) != ObjectPlacementSurfaceSupport.None;
}

