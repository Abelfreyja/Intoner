using Intoner.Objects.Catalog;
using Intoner.Objects.Models;

namespace Intoner.Objects.Runtime;

internal sealed class HousingPolicyRule(PlacementEvaluationFactory evaluationFactory) : IPlacementRule
{
    public bool TryEvaluate(
        PlacementValidationContext context,
        ObjectSnapshot snapshot,
        ObjectBoundsSnapshot boundsSnapshot,
        out PlacementEvaluation evaluation)
    {
        ObjectHousingModeState state = context.HousingModeState;
        if (snapshot.Kind != ObjectKind.Furniture)
        {
            evaluation = evaluationFactory.Invalid(
                snapshot,
                PlacementIssueCode.InvalidHousingObjectKind,
                "Housing mode only allows furniture objects.");
            return true;
        }

        if (snapshot.Model is not FurnitureModel furnitureModel
         || string.IsNullOrWhiteSpace(furnitureModel.SharedGroupPath))
        {
            evaluation = evaluationFactory.Invalid(
                snapshot,
                PlacementIssueCode.InvalidFurnitureModel,
                "Housing mode requires a valid furniture sgb path.");
            return true;
        }

        if (!context.TryGetMetadata(snapshot.Id, out HousingFurnitureMetadata metadata))
        {
            evaluation = evaluationFactory.Unknown(
                snapshot.Id,
                PlacementIssueCode.MissingFurnitureMetadata,
                "Furniture housing metadata is not available.");
            return true;
        }

        if (!HousingFurnitureAreaPolicy.AllowsArea(metadata, state.Area))
        {
            evaluation = evaluationFactory.Invalid(
                snapshot,
                PlacementIssueCode.FurnitureAreaMismatch,
                $"Furniture is {HousingFurnitureAreaPolicy.FormatArea(metadata)}-only, but Housing Mode is set to {HousingFurnitureAreaPolicy.FormatArea(state.Area)}.");
            return true;
        }

        if (context.IsFurnitureLimitOverflow(snapshot.Id))
        {
            evaluation = evaluationFactory.Invalid(
                snapshot,
                PlacementIssueCode.FurnitureLimitExceeded,
                $"Housing mode furniture limit exceeded ({context.FurnitureCount}/{state.FurnitureLimit}).");
            return true;
        }

        evaluation = default!;
        return false;
    }
}

