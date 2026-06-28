using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Runtime;

internal sealed class SurfaceRule(
    PlacementSurfaceResolver surfaceResolver,
    PlacementSurfaceContactValidator contactValidator,
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

        evaluation = metadata.Surface switch
        {
            HousingPlacementSurface.Wall  => EvaluateWallFurniture(context, snapshot, boundsSnapshot, metadata),
            HousingPlacementSurface.Floor => EvaluateFloorFurniture(context, snapshot, boundsSnapshot, metadata),
            _                             => EvaluateFloorLikeFurniture(context, snapshot, boundsSnapshot, metadata),
        };
        return evaluation.Status != PlacementValidationStatus.Valid;
    }

    private PlacementEvaluation EvaluateFloorFurniture(
        PlacementValidationContext context,
        ObjectSnapshot snapshot,
        ObjectBoundsSnapshot boundsSnapshot,
        HousingFurnitureMetadata metadata)
    {
        if (!PlacementSurfaceResolver.TryResolveNativePlacementClearanceRadius(boundsSnapshot, out float radius))
        {
            return evaluationFactory.Unknown(
                snapshot.Id,
                PlacementIssueCode.MissingFloorClearance,
                "Floor furniture native placement clearance is not available yet.");
        }

        if (!surfaceResolver.TryResolveSurface(context, snapshot, boundsSnapshot, metadata, out ObjectSurfaceHit hit, out PlacementIssueCode issueCode, out string surfaceError))
        {
            return evaluationFactory.Invalid(snapshot, issueCode, surfaceError);
        }

        if (!contactValidator.IsAlignedToSurface(snapshot, hit))
        {
            return evaluationFactory.Invalid(
                snapshot,
                PlacementIssueCode.NotAlignedToSurface,
                "Furniture is not aligned to its placement surface.");
        }

        return contactValidator.TryEvaluateFloorClearance(context, snapshot, radius, out issueCode, out string clearanceError)
            ? evaluationFactory.Invalid(snapshot, issueCode, clearanceError)
            : evaluationFactory.Valid(snapshot.Id);
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

        if (!surfaceResolver.TryResolveSurface(context, snapshot, boundsSnapshot, metadata, out ObjectSurfaceHit hit, out PlacementIssueCode issueCode, out string surfaceError))
        {
            return evaluationFactory.Invalid(snapshot, issueCode, surfaceError);
        }

        if (!contactValidator.IsAlignedToSurface(snapshot, hit))
        {
            return evaluationFactory.Invalid(
                snapshot,
                PlacementIssueCode.NotAlignedToSurface,
                "Furniture is not aligned to its placement surface.");
        }

        return evaluationFactory.Valid(snapshot.Id);
    }

    private PlacementEvaluation EvaluateWallFurniture(
        PlacementValidationContext context,
        ObjectSnapshot snapshot,
        ObjectBoundsSnapshot boundsSnapshot,
        HousingFurnitureMetadata metadata)
    {
        if (snapshot.Model is FurnitureModel { AttachmentParentId: not null })
        {
            return evaluationFactory.Valid(snapshot.Id);
        }

        if (!WallPlacementGeometry.TryResolveProbe(snapshot, boundsSnapshot, out WallPlacementProbe probe))
        {
            return evaluationFactory.Unknown(
                snapshot.Id,
                PlacementIssueCode.WallBoundsUnavailable,
                "Wall-mounted placement validation needs furniture bounds.");
        }

        if (contactValidator.TryValidateWallContact(context, snapshot, metadata, probe.Origin, probe.Direction, probe.ExpectedDistance, out PlacementIssueCode issueCode, out string errorMessage)
            || contactValidator.TryValidateWallContact(context, snapshot, metadata, probe.Origin, -probe.Direction, probe.ExpectedDistance, out issueCode, out errorMessage))
        {
            return evaluationFactory.Valid(snapshot.Id);
        }

        return evaluationFactory.Invalid(snapshot, issueCode, errorMessage);
    }
}

