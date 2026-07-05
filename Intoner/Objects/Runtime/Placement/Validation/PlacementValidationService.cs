using Intoner.Objects.Models;

namespace Intoner.Objects.Runtime;

internal sealed class PlacementValidationService(
    IObjectHousingModePolicy housingModePolicy,
    IObjectRuntimeLocationService locationService,
    PlacementValidationContextBuilder contextBuilder,
    PlacementRuleRunner ruleRunner,
    AttachmentHierarchyRule attachmentHierarchyRule,
    PlacementEvaluationFactory evaluationFactory)
{
    private static readonly IReadOnlyDictionary<Guid, PlacementEvaluation> EmptyEvaluations =
        new Dictionary<Guid, PlacementEvaluation>();

    private readonly Dictionary<Guid, CachedEvaluation> _cache = [];
    private readonly List<Guid> _staleCacheIds = [];

    public void ClearCache()
    {
        _cache.Clear();
        _staleCacheIds.Clear();
    }

    public IReadOnlyDictionary<Guid, PlacementEvaluation> Evaluate(
        IReadOnlyList<ObjectSnapshot> localSnapshots,
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots)
    {
        ObjectHousingModeState state = housingModePolicy.GetState();
        if (!state.IsHousingMode)
        {
            ClearCache();
            return EmptyEvaluations;
        }

        HousingPlacementContext housingPlacementContext = locationService.ResolveHousingPlacementContext(state);
        if (!housingPlacementContext.CanEvaluatePlacementPolicy)
        {
            ClearCache();
            return EmptyEvaluations;
        }

        PlacementValidationContext context = contextBuilder.Build(localSnapshots, boundsSnapshots, state, housingPlacementContext);
        Dictionary<Guid, PlacementEvaluation> evaluations = [];
        foreach (ObjectSnapshot snapshot in localSnapshots)
        {
            if (!context.TryGetBounds(snapshot.Id, out ObjectBoundsSnapshot boundsSnapshot))
            {
                evaluations[snapshot.Id] = evaluationFactory.Unknown(
                    snapshot.Id,
                    PlacementIssueCode.BoundsUnavailable,
                    "Object bounds are not available yet.");
                continue;
            }

            EvaluationCacheKey cacheKey = new(
                state,
                housingPlacementContext,
                snapshot,
                boundsSnapshot,
                context.FurnitureSetSignature,
                context.FootprintSignature,
                context.AttachmentSignature);
            if (_cache.TryGetValue(snapshot.Id, out CachedEvaluation cached)
                && cached.Key == cacheKey)
            {
                evaluations[snapshot.Id] = cached.Evaluation;
                continue;
            }

            PlacementEvaluation evaluation = ruleRunner.Evaluate(context, snapshot, boundsSnapshot);
            StoreCacheEntry(snapshot.Id, cacheKey, evaluation);
            evaluations[snapshot.Id] = evaluation;
        }

        attachmentHierarchyRule.Apply(context, evaluations);
        RemoveStaleCacheEntries(context.SnapshotsById);
        return evaluations;
    }

    private void RemoveStaleCacheEntries(IReadOnlyDictionary<Guid, ObjectSnapshot> snapshotsById)
    {
        _staleCacheIds.Clear();
        foreach (Guid cachedId in _cache.Keys)
        {
            if (!snapshotsById.ContainsKey(cachedId))
            {
                _staleCacheIds.Add(cachedId);
            }
        }

        foreach (Guid cachedId in _staleCacheIds)
        {
            _cache.Remove(cachedId);
        }

        _staleCacheIds.Clear();
    }

    private void StoreCacheEntry(Guid objectId, EvaluationCacheKey cacheKey, PlacementEvaluation evaluation)
    {
        if (!CanCache(evaluation))
        {
            _cache.Remove(objectId);
            return;
        }

        _cache[objectId] = new CachedEvaluation(cacheKey, evaluation);
    }

    private static bool CanCache(PlacementEvaluation evaluation)
    {
        if (evaluation.Status == PlacementValidationStatus.Unknown)
        {
            return false;
        }

        foreach (PlacementValidationIssue issue in evaluation.Issues)
        {
            if (IsTransientIssue(issue.Code))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTransientIssue(PlacementIssueCode issueCode)
        => issueCode is PlacementIssueCode.MissingPlacementSurface
            or PlacementIssueCode.HousingAreaUnavailable
            or PlacementIssueCode.HousingSizeUnavailable;

    private readonly record struct EvaluationCacheKey(
        ObjectHousingModeState State,
        HousingPlacementContext HousingPlacementContext,
        ObjectSnapshot Snapshot,
        ObjectBoundsSnapshot BoundsSnapshot,
        int FurnitureSetSignature,
        int FootprintSignature,
        int AttachmentSignature);

    private readonly record struct CachedEvaluation(
        EvaluationCacheKey Key,
        PlacementEvaluation Evaluation);
}

