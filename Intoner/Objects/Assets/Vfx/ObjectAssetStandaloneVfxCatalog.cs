using Intoner.Objects.Assets.Cache;
using Intoner.Objects.Resources;
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
            if (asset.Report is not { IsSupportedStandalone: true } report)
            {
                continue;
            }

            snapshot.Add(new RuntimeVfxAsset(
                asset.Path,
                report.CatalogSource,
                report.SearchTerms,
                report.Analysis.LoopFacts));
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
        string normalizedPath = ObjectResolvedPath.FromGamePath(path).Path;
        report = null;
        if (!GameAssetPathRules.IsFileKind(normalizedPath, GameAssetFileKind.Avfx))
        {
            return false;
        }

        RuntimeVfxEvidence resolvedEvidence = RuntimeVfxEvidence.None;
        _ = state.KnowledgeBase.TryGetPath(normalizedPath, out KnownAssetPath? knownPath);
        _ = state.StaticResolvedVfxPaths.TryGetValue(normalizedPath, out ResolvedVfxPath? resolvedPath);
        AssetPathContract pathContracts = knownPath?.Contracts ?? AssetPathContract.None;
        KnownVfxFamily familyHint = resolvedPath?.Family ?? knownPath?.VfxFamily ?? KnownVfxFamily.None;
        IReadOnlyList<string> knownSearchTerms = knownPath?.SearchTerms ?? [];

        if (resolvedPath is null && familyHint == KnownVfxFamily.None)
        {
            familyHint = KnownVfxFamilyExtensions.InferFamilyHintFromPath(normalizedPath);
        }

        if (resolvedPath is not null)
        {
            analysis ??= resolvedPath.Analysis;
            resolvedEvidence |= resolvedPath.Evidence;
            pathContracts |= resolvedPath.Contracts;
            knownSearchTerms = SearchTermUtility.MergeTerms(knownSearchTerms, resolvedPath.SearchTerms);
        }

        if (state.VfxAssets.TryGetValue(normalizedPath, out RuntimeVfxAssetState? existingAsset))
        {
            analysis ??= existingAsset.Analysis;
            resolvedEvidence |= existingAsset.SourceEvidence.WithoutAnalysisFlags();
            pathContracts |= existingAsset.Report?.PathContracts ?? AssetPathContract.None;
            if (familyHint == KnownVfxFamily.None)
            {
                familyHint = existingAsset.Report?.FamilyHint ?? KnownVfxFamily.None;
            }

            runtimeObserved |= existingAsset.SeenFromRuntime;
        }

        VfxTimelineReferenceInfo timelineReference = GetTimelineReferenceInfo(state, normalizedPath);
        RuntimeVfxEvidence sourceEvidence = (resolvedEvidence | evidence).WithoutAnalysisFlags() | timelineReference.NormalizedEvidence;
        if (analysis is null
         && (!_gameData.FileExists(normalizedPath)
          || !VfxAssetAnalyzer.TryAnalyzeAvfx(_gameData, normalizedPath, out analysis)))
        {
            return false;
        }

        report = CreateReport(
            normalizedPath,
            familyHint,
            pathContracts,
            sourceEvidence,
            timelineReference,
            knownSearchTerms,
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
        string normalizedPath = ObjectResolvedPath.FromGamePath(path).Path;

        if (!GameAssetPathRules.IsFileKind(normalizedPath, GameAssetFileKind.Avfx))
        {
            return ObservationApplyResult.None;
        }

        _ = AddKnowledgePath(state, normalizedPath, source, contract, searchTerms, familyHint);
        return TryPromote(
            state,
            normalizedPath,
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
        string normalizedPath = ObjectResolvedPath.FromGamePath(path).Path;

        if (!GameAssetPathRules.IsFileKind(normalizedPath, GameAssetFileKind.Avfx))
        {
            return ObservationApplyResult.None;
        }

        IReadOnlyList<string> referenceSearchTerms = SearchTermUtility.MergeTerms(searchTerms, referenceInfo.BuildSearchTerms());
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
            TryMergeTrackedEvidence(
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
        string normalizedPath = ObjectResolvedPath.FromGamePath(path).Path;

        if (!GameAssetPathRules.IsFileKind(normalizedPath, GameAssetFileKind.Avfx))
        {
            return ObservationApplyResult.None;
        }

        bool existed = state.VfxAssets.TryGetValue(normalizedPath, out RuntimeVfxAssetState? asset);
        asset ??= new RuntimeVfxAssetState(normalizedPath);
        RuntimeVfxEvidence previousEvidence = asset.Evidence;
        bool previousSeenFromRuntime = asset.SeenFromRuntime;
        VfxAnalysis? previousAnalysis = asset.Analysis;
        VfxStandaloneReport? previousReport = asset.Report;
        bool hasReport = TryBuildReport(state, normalizedPath, evidence, analysis, runtimeObserved, out VfxStandaloneReport? report);
        asset.SourceEvidence |= evidence.WithoutAnalysisFlags();
        asset.SeenFromRuntime |= runtimeObserved;
        asset.Analysis ??= analysis;

        if (hasReport && report is not null)
        {
            asset.SourceEvidence = report.SourceEvidence;
            asset.Analysis = report.Analysis;
            asset.SeenFromRuntime = report.SeenFromRuntime;
            asset.Report = report;
        }

        state.VfxAssets[normalizedPath] = asset;

        if (existed
         && asset.Evidence == previousEvidence
         && Equals(asset.Analysis, previousAnalysis)
         && asset.SeenFromRuntime == previousSeenFromRuntime
         && VfxStandaloneReport.HasSameClassification(previousReport, asset.Report))
        {
            return ObservationApplyResult.None;
        }

        bool projectionChanged = !VfxStandaloneReport.HasSameProjection(previousReport, asset.Report);

        if (projectionChanged)
        {
            MarkStandaloneVfxChanged(state);
        }
        else if (asset.SeenFromRuntime)
        {
            MarkCacheSectionsDirty(state, ObjectAssetCacheSectionSet.RuntimeVfx);
        }

        return projectionChanged
            ? ObservationApplyResult.ProjectionChanged
            : ObservationApplyResult.MetadataChanged;
    }

    private ObservationApplyResult TryMergeTrackedEvidence(
        CatalogAssetState state,
        string path,
        RuntimeVfxEvidence evidence,
        bool runtimeObserved = false)
    {
        string normalizedPath = ObjectResolvedPath.FromGamePath(path).Path;

        if (!state.VfxAssets.TryGetValue(normalizedPath, out RuntimeVfxAssetState? asset))
        {
            return ObservationApplyResult.None;
        }

        return TryPromote(state, normalizedPath, asset.Evidence | evidence, asset.Analysis, runtimeObserved || asset.SeenFromRuntime);
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
        IReadOnlyList<string> knownSearchTerms,
        VfxAnalysis analysis,
        bool runtimeObserved)
    {
        VfxStandalonePolicyResult policyResult = VfxStandalonePolicy.Evaluate(
            analysis,
            familyHint,
            sourceEvidence,
            timelineReference.Context,
            pathContracts);
        RuntimeVfxEvidence combinedEvidence = sourceEvidence | policyResult.AnalysisEvidence;
        string catalogSource = combinedEvidence.ResolveCatalogSourceLabel();
        IReadOnlyList<string> searchTerms = VfxCatalogSearchTerms.Build(
            path,
            catalogSource,
            familyHint,
            timelineReference,
            analysis.LoopFacts,
            knownSearchTerms);

        return new VfxStandaloneReport(
            path,
            catalogSource,
            familyHint,
            pathContracts,
            sourceEvidence,
            combinedEvidence,
            timelineReference,
            analysis,
            searchTerms,
            policyResult.SupportClass,
            runtimeObserved);
    }
}
