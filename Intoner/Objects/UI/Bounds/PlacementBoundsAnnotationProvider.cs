using Dalamud.Interface;
using Intoner.Objects.Runtime;

namespace Intoner.Objects.UI.Bounds;

internal sealed class PlacementBoundsAnnotationProvider
{
    private const BoundsAnnotationCorner DefaultCorner = BoundsAnnotationCorner.BottomRight;

    public void Append(
        IReadOnlyDictionary<Guid, PlacementEvaluation> evaluations,
        ICollection<BoundsAnnotation> annotations)
    {
        foreach (KeyValuePair<Guid, PlacementEvaluation> entry in evaluations)
        {
            PlacementEvaluation evaluation = entry.Value;
            if (evaluation.Status != PlacementValidationStatus.Invalid)
            {
                continue;
            }

            annotations.Add(new BoundsAnnotation(
                evaluation.ObjectId,
                DefaultCorner,
                FontAwesomeIcon.Exclamation,
                EditorColors.HousingPlacementInvalid,
                "Invalid housing placement",
                evaluation.Message,
                evaluation.Fixes));
        }
    }
}

