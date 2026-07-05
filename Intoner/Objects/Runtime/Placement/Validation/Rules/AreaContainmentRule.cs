using Intoner.Objects.Catalog;
using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.Runtime;

internal sealed class AreaContainmentRule(
    NativePlacementQuery nativeQuery,
    PlacementEvaluationFactory evaluationFactory) : IPlacementRule
{
    private const int MaxContainmentPointCount = 3;
    private const float DuplicatePointDistanceSquared = ObjectMathUtility.ScalarEpsilon * ObjectMathUtility.ScalarEpsilon;

    public bool TryEvaluate(
        PlacementValidationContext context,
        ObjectSnapshot snapshot,
        ObjectBoundsSnapshot boundsSnapshot,
        out PlacementEvaluation evaluation)
    {
        HousingPlacementContext placementContext = context.HousingPlacementContext;
        if (!placementContext.HasCurrentArea)
        {
            evaluation = evaluationFactory.Unknown(
                snapshot.Id,
                PlacementIssueCode.HousingAreaUnavailable,
                "Current housing area is not available.");
            return true;
        }

        if (placementContext.HasAreaMismatch)
        {
            evaluation = evaluationFactory.Invalid(
                snapshot,
                PlacementIssueCode.HousingMapAreaMismatch,
                $"Housing mode is set to {placementContext.TargetAreaName}, but the current map is {placementContext.CurrentAreaName}.");
            return true;
        }

        if (!placementContext.HasCurrentSize)
        {
            evaluation = evaluationFactory.Unknown(
                snapshot.Id,
                PlacementIssueCode.HousingSizeUnavailable,
                "Current housing size is not available.");
            return true;
        }

        if (placementContext.HasSizeMismatch)
        {
            evaluation = evaluationFactory.Invalid(
                snapshot,
                PlacementIssueCode.HousingSizeMismatch,
                $"Housing mode is set to {placementContext.TargetSizeName}, but the current housing size is {placementContext.CurrentSizeName}.");
            return true;
        }

        if (placementContext.CurrentArea == ObjectHousingArea.Indoor)
        {
            evaluation = default!;
            return false;
        }

        PlacementValidationStatus containmentStatus = CheckPlacementAreaContainment(context, snapshot, boundsSnapshot, placementContext);
        if (containmentStatus == PlacementValidationStatus.Invalid)
        {
            evaluation = evaluationFactory.Invalid(
                snapshot,
                PlacementIssueCode.OutsideHousingArea,
                "Furniture is outside the current housing placement area.");
            return true;
        }

        if (containmentStatus == PlacementValidationStatus.Unknown)
        {
            evaluation = evaluationFactory.Unknown(
                snapshot.Id,
                PlacementIssueCode.HousingAreaUnavailable,
                "Housing placement area containment is not available.");
            return true;
        }

        evaluation = default!;
        return false;
    }

    private PlacementValidationStatus CheckPlacementAreaContainment(
        PlacementValidationContext context,
        ObjectSnapshot snapshot,
        ObjectBoundsSnapshot boundsSnapshot,
        HousingPlacementContext placementContext)
    {
        Span<Vector3> queryPoints = stackalloc Vector3[MaxContainmentPointCount];
        int queryPointCount = BuildContainmentQueryPoints(context, snapshot, boundsSnapshot, queryPoints);
        bool hasUnknown = false;
        for (int index = 0; index < queryPointCount; ++index)
        {
            PlacementValidationStatus status = nativeQuery.CheckPlacementAreaContainment(placementContext, queryPoints[index]);
            if (status == PlacementValidationStatus.Valid)
            {
                return status;
            }

            hasUnknown |= status == PlacementValidationStatus.Unknown;
        }

        return hasUnknown
            ? PlacementValidationStatus.Unknown
            : PlacementValidationStatus.Invalid;
    }

    private static int BuildContainmentQueryPoints(
        PlacementValidationContext context,
        ObjectSnapshot snapshot,
        ObjectBoundsSnapshot boundsSnapshot,
        Span<Vector3> queryPoints)
    {
        int count = 0;
        if (context.TryGetMetadata(snapshot.Id, out HousingFurnitureMetadata metadata)
            && metadata.Surface == HousingPlacementSurface.Wall
            && WallPlacementGeometry.TryResolveContactPoints(snapshot, boundsSnapshot, out Vector3 forwardContactPoint, out Vector3 backwardContactPoint))
        {
            Vector3 direction = ObjectMathUtility.TryNormalize(forwardContactPoint - backwardContactPoint, out Vector3 normalizedDirection)
                ? normalizedDirection
                : Vector3.Zero;
            count = AddContainmentPoint(queryPoints, count, forwardContactPoint - (direction * PlacementValidationConstants.SurfaceAlignmentTolerance));
            count = AddContainmentPoint(queryPoints, count, backwardContactPoint + (direction * PlacementValidationConstants.SurfaceAlignmentTolerance));
        }

        return AddContainmentPoint(queryPoints, count, snapshot.Transform.Position);
    }

    private static int AddContainmentPoint(Span<Vector3> queryPoints, int count, Vector3 point)
    {
        if (count >= queryPoints.Length)
        {
            return count;
        }

        for (int index = 0; index < count; ++index)
        {
            if (Vector3.DistanceSquared(queryPoints[index], point) <= DuplicatePointDistanceSquared)
            {
                return count;
            }
        }

        queryPoints[count] = point;
        return count + 1;
    }
}

