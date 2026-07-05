using Intoner.Objects.Catalog;
using Intoner.Objects.Models;

namespace Intoner.Objects.Runtime;

internal static class PlacementSurfaceContactValidator
{
    public static PlacementValidationStatus ValidateFloorContact(
        PlacementValidationContext context,
        ObjectSnapshot snapshot,
        ObjectBoundsSnapshot boundsSnapshot,
        HousingFurnitureMetadata metadata,
        ObjectSurfaceHit hit,
        out PlacementIssueCode issueCode,
        out string errorMessage)
    {
        float surfaceOffset = snapshot.Transform.Position.Y - hit.Point.Y;
        return PlacementSurfaceFloatPolicy.EvaluateFloorOffset(
            context,
            metadata,
            boundsSnapshot,
            snapshot.Transform.Position.Y,
            surfaceOffset,
            out issueCode,
            out errorMessage);
    }
}
