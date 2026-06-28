using Intoner.Objects.Models;

namespace Intoner.Objects.Runtime;

internal sealed class AreaContainmentRule(
    NativePlacementQuery nativeQuery,
    PlacementEvaluationFactory evaluationFactory) : IPlacementRule
{
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

        PlacementValidationStatus containmentStatus = nativeQuery.CheckPlacementAreaContainment(placementContext, snapshot.Transform.Position);
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
}

