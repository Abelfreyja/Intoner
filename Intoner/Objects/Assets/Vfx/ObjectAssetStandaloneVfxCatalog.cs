using Intoner.Objects.Assets.Cache;
using Intoner.Objects.Utils;
using System.Diagnostics.CodeAnalysis;
using static Intoner.Objects.Assets.ObjectAssetStateChange;

namespace Intoner.Objects.Assets;

internal sealed class ObjectAssetStandaloneVfxCatalog(IObjectAssetGameData gameData)
{
    private readonly IObjectAssetGameData _gameData = gameData;

    public static RuntimeVfxAsset[] BuildSnapshot(
        CatalogAssetState state,
        IComparer<RuntimeVfxAsset> comparer)
    {
        List<RuntimeVfxAsset> snapshot = new(state.VfxAssets.Count);
        foreach (RuntimeVfxAssetState asset in state.VfxAssets.Values)
        {
            if (asset.SupportClass != VfxStandaloneSupportClass.SupportedStandalone)
            {
                continue;
            }

            snapshot.Add(new RuntimeVfxAsset(
                asset.Path,
                asset.GetPrimarySource(),
                asset.BuildSearchTerms()));
        }

        snapshot.Sort(comparer);
        return snapshot.ToArray();
    }

    public bool TryBuildReport(
        CatalogAssetState state,
        string path,
        RuntimeVfxEvidence evidence,
        VfxAnalysis? analysis,
        bool runtimeObserved,
        [NotNullWhen(true)] out VfxStandaloneReport? report)
    {
        string normalizedPath = ObjectPathRules.NormalizeGamePath(path);
        report = null;
        if (!ObjectPathRules.IsVfxPath(normalizedPath) || !_gameData.FileExists(normalizedPath))
        {
            return false;
        }

        RuntimeVfxEvidence resolvedEvidence = RuntimeVfxEvidence.None;
        AssetPathContract pathContracts = GetPathContracts(state, normalizedPath);
        KnownVfxFamily familyHint = GetPathFamilyHint(state, normalizedPath);
        if (state.StaticResolvedVfxPaths.TryGetValue(normalizedPath, out ResolvedVfxPath? resolvedPath))
        {
            analysis ??= resolvedPath.Analysis;
            resolvedEvidence |= resolvedPath.Evidence;
            pathContracts |= resolvedPath.Contracts;
            if (familyHint == KnownVfxFamily.None)
            {
                familyHint = resolvedPath.Family;
            }
        }

        if (state.VfxAssets.TryGetValue(normalizedPath, out RuntimeVfxAssetState? existingAsset))
        {
            analysis ??= existingAsset.Analysis;
            resolvedEvidence |= existingAsset.Evidence.WithoutAnalysisFlags();
            pathContracts |= existingAsset.PathContracts;
            if (familyHint == KnownVfxFamily.None)
            {
                familyHint = existingAsset.FamilyHint;
            }

            runtimeObserved |= existingAsset.SeenFromRuntime;
        }

        VfxTimelineReferenceInfo timelineReference = GetTimelineReferenceInfo(state, normalizedPath);
        RuntimeVfxEvidence sourceEvidence = (resolvedEvidence | evidence).WithoutAnalysisFlags() | timelineReference.NormalizedEvidence;
        if (analysis is null && !VfxAssetAnalyzer.TryAnalyzeAvfx(_gameData, normalizedPath, out analysis))
        {
            return false;
        }

        report = CreateReport(
            normalizedPath,
            familyHint,
            pathContracts,
            sourceEvidence,
            timelineReference,
            analysis,
            runtimeObserved);
        return true;
    }

    public ObservationApplyResult ObserveStaticCreate(CatalogAssetState state, string path)
        => ObserveStandalonePath(
            state,
            path,
            AssetPathSource.RuntimeObserved,
            AssetPathContract.RuntimeObservation,
            ["observed static"],
            RuntimeVfxEvidence.StaticCreate,
            runtimeObserved: true);

    public ObservationApplyResult ObserveActorCreate(CatalogAssetState state, string path)
        => ObserveStandalonePath(
            state,
            path,
            AssetPathSource.RuntimeObserved,
            AssetPathContract.RuntimeObservation,
            ["actor create"],
            RuntimeVfxEvidence.ActorCreate,
            runtimeObserved: true);

    public ObservationApplyResult ObserveTriggerUse(CatalogAssetState state, string path)
        => ObserveStandalonePath(
            state,
            path,
            AssetPathSource.RuntimeObserved,
            AssetPathContract.RuntimeObservation,
            ["trigger used"],
            RuntimeVfxEvidence.TriggerUsed,
            runtimeObserved: true);

    public ObservationApplyResult ObserveStandalonePath(
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
        return TryPromote(
            state,
            path,
            evidence,
            analysis,
            runtimeObserved);
    }

    public ObservationApplyResult MergeTimelineReference(
        CatalogAssetState state,
        string path,
        VfxTimelineReferenceInfo referenceInfo,
        AssetPathSource source,
        AssetPathContract contract,
        IReadOnlyList<string> searchTerms,
        bool runtimeObserved)
    {
        string normalizedPath = ObjectPathRules.NormalizeGamePath(path);
        IReadOnlyList<string> referenceSearchTerms = ObjectSearchTermUtility.MergeTerms(searchTerms, referenceInfo.BuildSearchTerms());
        _ = AddKnowledgePath(state, normalizedPath, source, contract, referenceSearchTerms);

        ObservationApplyResult result = ObservationApplyResult.None;
        if (runtimeObserved)
        {
            if (MergeTimelineReferenceInfo(state.RuntimeTimelineReferencedVfx, normalizedPath, referenceInfo))
            {
                MarkCacheSectionsDirty(state, ObjectAssetCacheSectionSet.TimelineReferencedVfx);
                result = Combine(result, ObservationApplyResult.MetadataChanged);
            }
        }
        else if (MergeTimelineReferenceInfo(state.StaticTimelineReferencedVfx, normalizedPath, referenceInfo))
        {
            result = Combine(result, ObservationApplyResult.MetadataChanged);
        }

        return Combine(
            result,
            TryMergeAcceptedEvidence(
                state,
                normalizedPath,
                referenceInfo.NormalizedEvidence,
                runtimeObserved));
    }

    public ObservationApplyResult TryPromote(
        CatalogAssetState state,
        string path,
        RuntimeVfxEvidence evidence,
        VfxAnalysis? analysis = null,
        bool runtimeObserved = false)
    {
        if (!TryBuildReport(state, path, evidence, analysis, runtimeObserved, out VfxStandaloneReport? report))
        {
            return ObservationApplyResult.None;
        }

        if (!report.IsSupportedStandalone)
        {
            string normalizedPath = ObjectPathRules.NormalizeGamePath(path);
            return RemoveStandaloneVfx(state, normalizedPath)
                ? ObservationApplyResult.ProjectionChanged
                : ObservationApplyResult.None;
        }

        if (!state.VfxAssets.TryGetValue(report.Path, out RuntimeVfxAssetState? asset))
        {
            state.VfxAssets[report.Path] = new RuntimeVfxAssetState(report.Path)
            {
                CatalogSource       = report.CatalogSource,
                Evidence            = report.CombinedEvidence,
                Analysis            = report.Analysis,
                PathContracts       = report.PathContracts,
                FamilyHint          = report.FamilyHint,
                SupportClass        = report.SupportClass,
                UnsupportedReasons  = report.UnsupportedReasons,
                ContextClues        = report.ContextClues,
                UnknownReasons      = report.UnknownReasons,
                SeenFromRuntime     = report.SeenFromRuntime,
            };
            MarkStandaloneVfxChanged(state);
            return ObservationApplyResult.ProjectionChanged;
        }

        RuntimeVfxEvidence previousEvidence = asset.Evidence;
        string previousCatalogSource = asset.CatalogSource;
        bool previousSeenFromRuntime = asset.SeenFromRuntime;
        VfxAnalysis? previousAnalysis = asset.Analysis;
        KnownVfxFamily previousFamilyHint = asset.FamilyHint;
        VfxStandaloneSupportClass previousSupportClass = asset.SupportClass;

        asset.Evidence = report.CombinedEvidence;
        asset.Analysis = report.Analysis;
        asset.PathContracts = report.PathContracts;
        asset.FamilyHint = report.FamilyHint;
        asset.CatalogSource = report.CatalogSource;
        asset.SupportClass = report.SupportClass;
        asset.UnsupportedReasons = report.UnsupportedReasons;
        asset.ContextClues = report.ContextClues;
        asset.UnknownReasons = report.UnknownReasons;
        asset.SeenFromRuntime = report.SeenFromRuntime;
        if (asset.Evidence == previousEvidence
         && Equals(asset.Analysis, previousAnalysis)
         && string.Equals(asset.CatalogSource, previousCatalogSource, StringComparison.OrdinalIgnoreCase)
         && asset.FamilyHint == previousFamilyHint
         && asset.SupportClass == previousSupportClass
         && asset.SeenFromRuntime == previousSeenFromRuntime)
        {
            return ObservationApplyResult.None;
        }

        asset.InvalidateSearchTerms();
        MarkStandaloneVfxChanged(state);

        bool projectionChanged = asset.Evidence != previousEvidence
            || !string.Equals(asset.CatalogSource, previousCatalogSource, StringComparison.OrdinalIgnoreCase)
            || asset.FamilyHint != previousFamilyHint
            || asset.SupportClass != previousSupportClass;

        return projectionChanged
            ? ObservationApplyResult.ProjectionChanged
            : ObservationApplyResult.MetadataChanged;
    }

    private ObservationApplyResult TryMergeAcceptedEvidence(
        CatalogAssetState state,
        string path,
        RuntimeVfxEvidence evidence,
        bool runtimeObserved = false)
    {
        string normalizedPath = ObjectPathRules.NormalizeGamePath(path);
        if (!state.VfxAssets.TryGetValue(normalizedPath, out RuntimeVfxAssetState? asset))
        {
            return ObservationApplyResult.None;
        }

        return TryPromote(state, normalizedPath, asset.Evidence | evidence, asset.Analysis, runtimeObserved || asset.SeenFromRuntime);
    }

    private static bool RemoveStandaloneVfx(CatalogAssetState state, string normalizedPath)
    {
        if (!state.VfxAssets.Remove(normalizedPath))
        {
            return false;
        }

        MarkStandaloneVfxChanged(state);
        return true;
    }

    private static AssetPathContract GetPathContracts(CatalogAssetState state, string path)
        => state.KnowledgeBase.TryGetPath(path, out KnownAssetPath? knownPath)
            ? knownPath.Contracts
            : AssetPathContract.None;

    private static KnownVfxFamily GetPathFamilyHint(CatalogAssetState state, string path)
    {
        if (state.StaticResolvedVfxPaths.TryGetValue(path, out ResolvedVfxPath? resolvedPath))
        {
            return resolvedPath.Family;
        }

        if (state.KnowledgeBase.TryGetPath(path, out KnownAssetPath? knownPath))
        {
            return knownPath.VfxFamily;
        }

        return KnownVfxFamilyExtensions.InferFamilyHintFromPath(path);
    }

    private static VfxTimelineReferenceInfo GetTimelineReferenceInfo(CatalogAssetState state, string path)
    {
        VfxTimelineReferenceInfo referenceInfo = VfxTimelineReferenceInfo.None;
        if (state.StaticTimelineReferencedVfx.TryGetValue(path, out VfxTimelineReferenceInfo staticReference))
        {
            referenceInfo = referenceInfo.Merge(staticReference);
        }

        if (state.RuntimeTimelineReferencedVfx.TryGetValue(path, out VfxTimelineReferenceInfo runtimeReference))
        {
            referenceInfo = referenceInfo.Merge(runtimeReference);
        }

        return referenceInfo;
    }

    private static bool MergeTimelineReferenceInfo(
        IDictionary<string, VfxTimelineReferenceInfo> timelineReferences,
        string path,
        VfxTimelineReferenceInfo referenceInfo)
    {
        VfxTimelineReferenceInfo normalizedReference = new(referenceInfo.NormalizedEvidence, referenceInfo.Context);
        if (!normalizedReference.HasEvidence)
        {
            return false;
        }

        if (!timelineReferences.TryGetValue(path, out VfxTimelineReferenceInfo existingReference))
        {
            timelineReferences.Add(path, normalizedReference);
            return true;
        }

        VfxTimelineReferenceInfo combinedReference = existingReference.Merge(normalizedReference);
        if (combinedReference == existingReference)
        {
            return false;
        }

        timelineReferences[path] = combinedReference;
        return true;
    }

    private static VfxStandaloneReport CreateReport(
        string path,
        KnownVfxFamily familyHint,
        AssetPathContract pathContracts,
        RuntimeVfxEvidence sourceEvidence,
        VfxTimelineReferenceInfo timelineReference,
        VfxAnalysis analysis,
        bool runtimeObserved)
    {
        VfxStandalonePolicyResult policyResult = VfxStandalonePolicy.Evaluate(
            path,
            analysis,
            sourceEvidence,
            timelineReference.Context,
            pathContracts);
        RuntimeVfxEvidence combinedEvidence = sourceEvidence | policyResult.AnalysisEvidence;

        return new VfxStandaloneReport(
            path,
            combinedEvidence.ResolveCatalogSourceLabel(),
            familyHint,
            pathContracts,
            sourceEvidence,
            combinedEvidence,
            timelineReference,
            analysis,
            policyResult.SupportClass,
            policyResult.UnsupportedReasons,
            policyResult.ContextClues,
            policyResult.UnknownReasons,
            runtimeObserved);
    }
}
