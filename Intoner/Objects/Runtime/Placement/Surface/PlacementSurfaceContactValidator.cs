using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.Runtime;

internal sealed class PlacementSurfaceContactValidator(PlacementSceneRaycaster sceneRaycaster)
{
    private static readonly Vector3[] FloorClearanceDirections =
    [
        Vector3.UnitX,
        -Vector3.UnitX,
        Vector3.UnitZ,
        -Vector3.UnitZ,
    ];

    public bool IsAlignedToSurface(ObjectSnapshot snapshot, ObjectSurfaceHit hit)
        => MathF.Abs(snapshot.Transform.Position.Y - hit.Point.Y) <= PlacementValidationConstants.SurfaceAlignmentTolerance;

    public bool TryEvaluateFloorClearance(
        PlacementValidationContext context,
        ObjectSnapshot snapshot,
        float radius,
        out PlacementIssueCode issueCode,
        out string errorMessage)
    {
        Vector3 position = snapshot.Transform.Position;
        for (int index = 0; index < FloorClearanceDirections.Length; ++index)
        {
            Vector3 direction = FloorClearanceDirections[index];
            Vector3 rayOrigin = position - (direction * radius) + (Vector3.UnitY * PlacementValidationConstants.NativeRayLift);
            PlacementSceneRaycastRequest request = new(
                snapshot.Id,
                rayOrigin,
                direction,
                PlacementValidationConstants.NativeRayMaxDistance);
            if (!sceneRaycaster.TryRaycastAny(context, request, out ObjectSurfaceHit hit))
            {
                continue;
            }

            Vector2 offset = new(position.X - hit.Point.X, position.Z - hit.Point.Z);
            if (offset.Length() < radius)
            {
                issueCode = PlacementIssueCode.MissingFloorClearance;
                errorMessage = "Floor furniture does not have native placement clearance.";
                return true;
            }
        }

        issueCode = PlacementIssueCode.None;
        errorMessage = string.Empty;
        return false;
    }

    public bool TryValidateWallContact(
        PlacementValidationContext context,
        ObjectSnapshot snapshot,
        HousingFurnitureMetadata metadata,
        Vector3 probeOrigin,
        Vector3 direction,
        float expectedDistance,
        out PlacementIssueCode issueCode,
        out string errorMessage)
    {
        PlacementSceneRaycastRequest request = new(
            snapshot.Id,
            probeOrigin,
            direction,
            PlacementValidationConstants.NativeRayMaxDistance,
            PlacementSurfacePolicy.ResolveAllowedMaterialMask(metadata));
        if (!sceneRaycaster.TryRaycastAny(context, request, out ObjectSurfaceHit hit)
            || hit.Material == 0)
        {
            issueCode = PlacementIssueCode.WallSurfaceUnavailable;
            errorMessage = "Wall-mounted furniture requires a wall placement surface.";
            return false;
        }

        if (!PlacementSurfacePolicy.TryValidateSurface(metadata, hit, out errorMessage))
        {
            issueCode = PlacementIssueCode.InvalidPlacementSurface;
            return false;
        }

        if (expectedDistance > 0f && hit.Distance - expectedDistance > PlacementValidationConstants.SurfaceAlignmentTolerance)
        {
            issueCode = PlacementIssueCode.WallFloatingFromSurface;
            errorMessage = "Wall-mounted furniture is floating away from its placement surface.";
            return false;
        }

        if (!ObjectMathUtility.TryNormalize(hit.Normal, out Vector3 surfaceNormal)
            || Vector3.Dot(surfaceNormal, -direction) < WallPlacementGeometry.NormalDotThreshold)
        {
            issueCode = PlacementIssueCode.WallNormalMismatch;
            errorMessage = "Wall-mounted furniture requires a matching wall surface normal.";
            return false;
        }

        issueCode = PlacementIssueCode.None;
        errorMessage = string.Empty;
        return true;
    }
}

