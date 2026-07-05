using Intoner.Objects.Catalog;
using Intoner.Objects.Models;

namespace Intoner.Objects.Runtime;

internal sealed class AttachedSurfaceRule(
    PlacementEvaluationFactory evaluationFactory) : IPlacementRule
{
    public bool TryEvaluate(
        PlacementValidationContext context,
        ObjectSnapshot snapshot,
        ObjectBoundsSnapshot boundsSnapshot,
        out PlacementEvaluation evaluation)
    {
        evaluation = default!;
        if (!context.TryGetMetadata(snapshot.Id, out HousingFurnitureMetadata metadata)
            || metadata.Surface != HousingPlacementSurface.Tabletop
            || snapshot.Model is not FurnitureModel { AttachmentParentId: not null })
        {
            return false;
        }

        if (!SurfaceAttachmentService.TryResolveAttachedParent(
                snapshot,
                context.SnapshotsById,
                context.BoundsById,
                out _,
                out ObjectBoundsSnapshot childBounds,
                out ObjectBoundsSnapshot parentBounds,
                out PlacementIssueCode issueCode,
                out string errorMessage))
        {
            evaluation = evaluationFactory.Unknown(
                snapshot,
                issueCode,
                errorMessage);
            return true;
        }

        if (!SurfaceAttachmentService.TryValidateAttachedTabletopPlacement(
                childBounds,
                parentBounds,
                out issueCode,
                out errorMessage))
        {
            evaluation = evaluationFactory.Invalid(snapshot, issueCode, errorMessage);
            return true;
        }

        return false;
    }
}

