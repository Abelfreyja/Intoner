using Intoner.Objects.Models;

namespace Intoner.Objects.Runtime;

internal sealed class AttachmentHierarchyRule(PlacementEvaluationFactory evaluationFactory)
{
    public void Apply(
        PlacementValidationContext context,
        Dictionary<Guid, PlacementEvaluation> evaluations)
    {
        Dictionary<Guid, PlacementEvaluation> resolvedEvaluations = [];
        foreach (ObjectSnapshot snapshot in context.Snapshots)
        {
            if (!evaluations.ContainsKey(snapshot.Id))
            {
                continue;
            }

            evaluations[snapshot.Id] = ResolveAttachmentEvaluation(
                snapshot.Id,
                context.SnapshotsById,
                evaluations,
                resolvedEvaluations,
                []);
        }
    }

    private PlacementEvaluation ResolveAttachmentEvaluation(
        Guid objectId,
        IReadOnlyDictionary<Guid, ObjectSnapshot> snapshotsById,
        IReadOnlyDictionary<Guid, PlacementEvaluation> evaluations,
        Dictionary<Guid, PlacementEvaluation> resolvedEvaluations,
        List<Guid> evaluationPath)
    {
        if (resolvedEvaluations.TryGetValue(objectId, out PlacementEvaluation? resolvedEvaluation)
            && resolvedEvaluation is not null)
        {
            return resolvedEvaluation;
        }

        int cycleStartIndex = evaluationPath.IndexOf(objectId);
        if (cycleStartIndex >= 0)
        {
            return MarkAttachmentCycle(evaluationPath, cycleStartIndex, snapshotsById, resolvedEvaluations);
        }

        if (!evaluations.TryGetValue(objectId, out PlacementEvaluation? evaluation))
        {
            PlacementEvaluation missingEvaluation = evaluationFactory.Unknown(
                objectId,
                PlacementIssueCode.NotEvaluated,
                "Furniture placement has not been evaluated.");
            resolvedEvaluations[objectId] = missingEvaluation;
            return missingEvaluation;
        }

        if (!snapshotsById.TryGetValue(objectId, out ObjectSnapshot? snapshot)
            || snapshot is null
            || snapshot.Model is not FurnitureModel { AttachmentParentId: { } parentId })
        {
            resolvedEvaluations[objectId] = evaluation;
            return evaluation;
        }

        evaluationPath.Add(objectId);
        PlacementEvaluation attachmentEvaluation = ResolveParentAttachmentEvaluation(
            snapshot,
            parentId,
            evaluation,
            snapshotsById,
            evaluations,
            resolvedEvaluations,
            evaluationPath);

        if (resolvedEvaluations.TryGetValue(objectId, out PlacementEvaluation? recursiveEvaluation)
            && recursiveEvaluation is not null
            && HasIssue(recursiveEvaluation, PlacementIssueCode.AttachmentCycle))
        {
            evaluationPath.RemoveAt(evaluationPath.Count - 1);
            return recursiveEvaluation;
        }

        evaluationPath.RemoveAt(evaluationPath.Count - 1);
        resolvedEvaluations[objectId] = attachmentEvaluation;
        return attachmentEvaluation;
    }

    private PlacementEvaluation ResolveParentAttachmentEvaluation(
        ObjectSnapshot snapshot,
        Guid parentId,
        PlacementEvaluation evaluation,
        IReadOnlyDictionary<Guid, ObjectSnapshot> snapshotsById,
        IReadOnlyDictionary<Guid, PlacementEvaluation> evaluations,
        Dictionary<Guid, PlacementEvaluation> resolvedEvaluations,
        List<Guid> evaluationPath)
    {
        if (!snapshotsById.TryGetValue(parentId, out ObjectSnapshot? parentSnapshot)
            || parentSnapshot is null
            || parentSnapshot.Kind != ObjectKind.Furniture)
        {
            return evaluationFactory.Unknown(
                snapshot,
                PlacementIssueCode.MissingAttachmentParent,
                "Furniture attachment parent is not available in the current layout.");
        }

        PlacementEvaluation parentEvaluation = ResolveAttachmentEvaluation(
            parentId,
            snapshotsById,
            evaluations,
            resolvedEvaluations,
            evaluationPath);

        if (parentEvaluation.Status == PlacementValidationStatus.Invalid)
        {
            return evaluationFactory.Invalid(
                snapshot,
                PlacementIssueCode.ParentPlacementInvalid,
                "Furniture attachment parent placement is invalid.");
        }

        if (parentEvaluation.Status == PlacementValidationStatus.Unknown
            && evaluation.Status == PlacementValidationStatus.Valid)
        {
            return evaluationFactory.Unknown(
                snapshot,
                PlacementIssueCode.ParentPlacementUnknown,
                "Furniture attachment parent placement is not verified.");
        }

        return evaluation;
    }

    private PlacementEvaluation MarkAttachmentCycle(
        List<Guid> evaluationPath,
        int cycleStartIndex,
        IReadOnlyDictionary<Guid, ObjectSnapshot> snapshotsById,
        Dictionary<Guid, PlacementEvaluation> resolvedEvaluations)
    {
        PlacementEvaluation? firstCycleEvaluation = null;
        for (int index = cycleStartIndex; index < evaluationPath.Count; ++index)
        {
            Guid cycleObjectId = evaluationPath[index];
            if (!snapshotsById.TryGetValue(cycleObjectId, out ObjectSnapshot? cycleSnapshot)
                || cycleSnapshot is null)
            {
                continue;
            }

            PlacementEvaluation cycleEvaluation = evaluationFactory.Invalid(
                cycleSnapshot,
                PlacementIssueCode.AttachmentCycle,
                "Furniture attachment hierarchy contains a cycle.");
            resolvedEvaluations[cycleObjectId] = cycleEvaluation;
            firstCycleEvaluation ??= cycleEvaluation;
        }

        return firstCycleEvaluation
               ?? evaluationFactory.Unknown(
                   evaluationPath[cycleStartIndex],
                   PlacementIssueCode.NotEvaluated,
                   "Furniture placement has not been evaluated.");
    }

    private static bool HasIssue(PlacementEvaluation evaluation, PlacementIssueCode issueCode)
    {
        foreach (PlacementValidationIssue issue in evaluation.Issues)
        {
            if (issue.Code == issueCode)
            {
                return true;
            }
        }

        return false;
    }
}

