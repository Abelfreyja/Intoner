using Intoner.Objects.Assets.Cache;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Assets;

internal sealed partial class ObjectAssetIndex
{
    private ObservationApplyResult ObserveBgModelPath(
        CatalogAssetState state,
        string path,
        AssetPathSource source,
        AssetPathContract contract,
        IReadOnlyList<string> searchTerms,
        string catalogSource,
        in ObjectTerritoryMetadata territoryMetadata)
    {
        _ = AddKnowledgePath(state, path, source, contract, searchTerms);
        return TryAddBgModel(state, path, catalogSource, territoryMetadata)
            ? ObservationApplyResult.ProjectionChanged
            : ObservationApplyResult.None;
    }

    private ObservationApplyResult ObserveStandaloneVfxPath(
        CatalogAssetState state,
        string path,
        AssetPathSource source,
        AssetPathContract contract,
        IReadOnlyList<string> searchTerms,
        RuntimeVfxEvidence evidence,
        bool runtimeObserved = false,
        VfxAnalysis? analysis = null,
        KnownVfxFamily familyHint = KnownVfxFamily.None)
    {
        _ = AddKnowledgePath(state, path, source, contract, searchTerms, familyHint);
        return TryPromoteStandaloneVfx(
            state,
            path,
            evidence,
            analysis,
            runtimeObserved);
    }

    private ObservationApplyResult MergeTimelineVfxReference(
        CatalogAssetState state,
        string path,
        VfxTimelineReferenceInfo referenceInfo,
        AssetPathSource source,
        AssetPathContract contract,
        IReadOnlyList<string> searchTerms,
        bool runtimeObserved)
    {
        IReadOnlyList<string> referenceSearchTerms = ObjectSearchTermUtility.MergeTerms(searchTerms, referenceInfo.BuildSearchTerms());
        _ = AddKnowledgePath(state, path, source, contract, referenceSearchTerms);

        ObservationApplyResult result = ObservationApplyResult.None;
        if (runtimeObserved)
        {
            if (MergeTimelineReferenceInfo(state.RuntimeTimelineReferencedVfx, path, referenceInfo))
            {
                MarkCacheSectionsDirty(state, ObjectAssetCacheSectionSet.TimelineReferencedVfx);
                result = Combine(result, ObservationApplyResult.MetadataChanged);
            }
        }
        else if (MergeTimelineReferenceInfo(state.StaticTimelineReferencedVfx, path, referenceInfo))
        {
            result = Combine(result, ObservationApplyResult.MetadataChanged);
        }

        return Combine(
            result,
            TryMergeAcceptedVfxEvidence(
                state,
                path,
                referenceInfo.NormalizedEvidence,
                runtimeObserved));
    }
}

