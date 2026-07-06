using Dalamud.Interface;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private bool TryResolveHousingPlacementEvaluation(ObjectSnapshot? snapshot, out PlacementEvaluation evaluation)
    {
        evaluation = default!;
        if (snapshot is null
            || !_placementEvaluations.TryGetValue(snapshot.Id, out PlacementEvaluation? resolvedEvaluation)
            || resolvedEvaluation is null)
        {
            return false;
        }

        evaluation = resolvedEvaluation;
        return true;
    }

    private static EditorHeroCard.Status CreateHousingPlacementHeroStatus(
        PlacementEvaluation evaluation,
        PlacementFixProposal? fix)
        => new(
            ResolveHousingPlacementIcon(evaluation.Status),
            "Housing Placement",
            ResolveHousingPlacementSummaryText(evaluation),
            ResolveHousingPlacementAccent(evaluation.Status),
            ResolveHousingPlacementMessageColor(evaluation),
            fix.HasValue ? CreateHousingPlacementHeroAction(fix.Value) : null);

    private static PlacementFixProposal? ResolveHousingPlacementHeroFix(PlacementEvaluation evaluation)
        => evaluation.Status == PlacementValidationStatus.Invalid && evaluation.Fixes.Count > 0
            ? evaluation.Fixes[0]
            : null;

    private static EditorHeroCard.StatusAction CreateHousingPlacementHeroAction(PlacementFixProposal fix)
        => new(
            ResolvePlacementFixIcon(fix.Kind),
            fix.Label,
            fix.Description,
            ResolvePlacementFixAccent(fix.Kind));

    private static string ResolveHousingPlacementSummaryText(PlacementEvaluation evaluation)
    {
        if (!string.IsNullOrWhiteSpace(evaluation.Message))
        {
            return evaluation.Message;
        }

        if (evaluation.Issues.Count > 0)
        {
            return evaluation.Issues[0].Message;
        }

        return evaluation.Status switch
        {
            PlacementValidationStatus.Valid   => "Placement satisfies the current housing rules.",
            PlacementValidationStatus.Invalid => "Placement does not satisfy the current housing rules.",
            _                                 => "Placement status is not available.",
        };
    }

    private bool TryApplyPlacementFix(ObjectSnapshot snapshot, PlacementFixProposal fix)
    {
        CommitPendingHistory();
        return _placementFixExecutor.TryApply(snapshot, fix);
    }

    private static FontAwesomeIcon ResolveHousingPlacementIcon(PlacementValidationStatus status)
        => status switch
        {
            PlacementValidationStatus.Valid   => FontAwesomeIcon.CheckCircle,
            PlacementValidationStatus.Invalid => FontAwesomeIcon.ExclamationTriangle,
            _                                  => FontAwesomeIcon.QuestionCircle,
        };

    private static Vector4 ResolveHousingPlacementAccent(PlacementValidationStatus status)
        => status switch
        {
            PlacementValidationStatus.Valid   => EditorColors.AccentGreen,
            PlacementValidationStatus.Invalid => EditorColors.HousingPlacementInvalid,
            _                                  => EditorColors.AccentYellow,
        };

    private static Vector4 ResolveHousingPlacementMessageColor(PlacementEvaluation evaluation)
        => evaluation.Status == PlacementValidationStatus.Invalid
            ? EditorColors.WithAlpha(ResolveHousingPlacementAccent(evaluation.Status), 0.95f)
            : EditorColors.TextDisabled;

    private static FontAwesomeIcon ResolvePlacementFixIcon(PlacementFixKind kind)
        => kind switch
        {
            PlacementFixKind.SnapToSurface         => FontAwesomeIcon.ArrowDown,
            PlacementFixKind.MoveToPlayerPlacement => FontAwesomeIcon.Running,
            PlacementFixKind.ClearAttachmentParent => FontAwesomeIcon.Unlink,
            _                                      => FontAwesomeIcon.Magic,
        };

    private static Vector4 ResolvePlacementFixAccent(PlacementFixKind kind)
        => kind switch
        {
            PlacementFixKind.SnapToSurface         => EditorColors.AccentBlue,
            PlacementFixKind.MoveToPlayerPlacement => EditorColors.AccentGreen,
            PlacementFixKind.ClearAttachmentParent => EditorColors.AccentYellow,
            _                                      => EditorColors.AccentPurple,
        };
}
