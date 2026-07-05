using Intoner.Objects.Catalog;
using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Models;

namespace Intoner.Objects.Runtime;

internal sealed class SurfaceRule(
    PlacementSurfaceResolver surfaceResolver,
    PlacementEvaluationFactory evaluationFactory) : IPlacementRule
{
    public bool TryEvaluate(
        PlacementValidationContext context,
        ObjectSnapshot snapshot,
        ObjectBoundsSnapshot boundsSnapshot,
        out PlacementEvaluation evaluation)
    {
        if (!context.TryGetMetadata(snapshot.Id, out HousingFurnitureMetadata metadata))
        {
            evaluation = default!;
            return false;
        }

        if (metadata.Surface == HousingPlacementSurface.Wall)
        {
            evaluation = default!;
            return false;
        }

        evaluation = metadata.Surface switch
        {
            HousingPlacementSurface.Floor => EvaluateFloorContact(context, snapshot, boundsSnapshot, metadata),
            _                             => EvaluateFloorLikeFurniture(context, snapshot, boundsSnapshot, metadata),
        };
        return evaluation.Status != PlacementValidationStatus.Valid;
    }

    private PlacementEvaluation EvaluateFloorLikeFurniture(
        PlacementValidationContext context,
        ObjectSnapshot snapshot,
        ObjectBoundsSnapshot boundsSnapshot,
        HousingFurnitureMetadata metadata)
    {
        if (metadata.Surface == HousingPlacementSurface.Tabletop
            && snapshot.Model is FurnitureModel { AttachmentParentId: not null })
        {
            return evaluationFactory.Valid(snapshot.Id);
        }

        return EvaluateFloorContact(context, snapshot, boundsSnapshot, metadata);
    }

    private PlacementEvaluation EvaluateFloorContact(
        PlacementValidationContext context,
        ObjectSnapshot snapshot,
        ObjectBoundsSnapshot boundsSnapshot,
        HousingFurnitureMetadata metadata)
    {
        if (!surfaceResolver.TryResolveSurface(context, snapshot, boundsSnapshot, metadata, out ObjectSurfaceHit hit, out PlacementIssueCode issueCode, out string surfaceError))
        {
            if (AllowsMissingIndoorSurface(context, metadata, issueCode))
            {
                return evaluationFactory.Valid(snapshot.Id);
            }

            return evaluationFactory.Invalid(snapshot, issueCode, surfaceError);
        }

        PlacementValidationStatus contactStatus = PlacementSurfaceContactValidator.ValidateFloorContact(
            context,
            snapshot,
            boundsSnapshot,
            metadata,
            hit,
            out issueCode,
            out string contactError);
        if (contactStatus == PlacementValidationStatus.Unknown)
        {
            return evaluationFactory.Unknown(snapshot.Id, issueCode, contactError);
        }

        if (contactStatus == PlacementValidationStatus.Invalid)
        {
            return evaluationFactory.Invalid(snapshot, issueCode, contactError);
        }

        return evaluationFactory.Valid(snapshot.Id);
    }

    private static bool AllowsMissingIndoorSurface(
        PlacementValidationContext context,
        HousingFurnitureMetadata metadata,
        PlacementIssueCode issueCode)
        => issueCode == PlacementIssueCode.MissingPlacementSurface
        && metadata.IsIndoor
        && context.HousingPlacementContext.CurrentArea == ObjectHousingArea.Indoor;
}

