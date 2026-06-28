using System.Diagnostics.CodeAnalysis;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Assets;

internal sealed partial class ObjectAssetIndex
{
    public IReadOnlyList<RuntimeVfxAsset> GetStandaloneVfxAssets(CancellationToken cancellationToken = default)
    {
        CatalogAssetState state = _warmupState.GetValue(cancellationToken);
        lock (_stateLock)
        {
            if (!state.StandaloneVfxSnapshotDirty && state.StandaloneVfxSnapshot is not null)
            {
                return state.StandaloneVfxSnapshot;
            }

            RuntimeVfxAsset[] snapshot = BuildStandaloneVfxSnapshot(state);
            state.StandaloneVfxSnapshot = snapshot;
            state.StandaloneVfxSnapshotDirty = false;
            return snapshot;
        }
    }

    public bool TryGetStandaloneVfxReport(string vfxPath, [NotNullWhen(true)] out VfxStandaloneReport? report)
    {
        CatalogAssetState state = _warmupState.GetValue();
        lock (_stateLock)
        {
            return TryBuildStandaloneVfxReport(
                state,
                vfxPath,
                RuntimeVfxEvidence.None,
                analysis: null,
                runtimeObserved: false,
                out report);
        }
    }

    private static RuntimeVfxAsset[] BuildStandaloneVfxSnapshot(CatalogAssetState state)
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

        snapshot.Sort(RuntimeVfxAssetComparer);
        return snapshot.ToArray();
    }

    private ObservationApplyResult TryPromoteStandaloneVfx(
        CatalogAssetState state,
        string path,
        RuntimeVfxEvidence evidence,
        VfxAnalysis? analysis = null,
        bool runtimeObserved = false)
    {
        if (!TryBuildStandaloneVfxReport(state, path, evidence, analysis, runtimeObserved, out VfxStandaloneReport? report))
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
                CatalogSource = report.CatalogSource,
                Evidence = report.CombinedEvidence,
                Analysis = report.Analysis,
                PathContracts = report.PathContracts,
                FamilyHint = report.FamilyHint,
                SupportClass = report.SupportClass,
                UnsupportedReasons = report.UnsupportedReasons,
                ContextClues = report.ContextClues,
                UnknownReasons = report.UnknownReasons,
                SeenFromRuntime = report.SeenFromRuntime,
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

    private bool TryBuildStandaloneVfxReport(
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

        report = VfxStandaloneReportFactory.Create(
            normalizedPath,
            familyHint,
            pathContracts,
            sourceEvidence,
            timelineReference,
            analysis,
            runtimeObserved);
        return true;
    }

    private ObservationApplyResult ObserveStaticVfxCreate(CatalogAssetState state, string path)
        => ObserveStandaloneVfxPath(
            state,
            path,
            AssetPathSource.RuntimeObserved,
            AssetPathContract.RuntimeObservation,
            ["observed static"],
            RuntimeVfxEvidence.StaticCreate,
            runtimeObserved: true);

    private ObservationApplyResult ObserveActorVfxCreate(CatalogAssetState state, string path)
        => ObserveStandaloneVfxPath(
            state,
            path,
            AssetPathSource.RuntimeObserved,
            AssetPathContract.RuntimeObservation,
            ["actor create"],
            RuntimeVfxEvidence.ActorCreate,
            runtimeObserved: true);

    private ObservationApplyResult ObserveTriggerUse(CatalogAssetState state, string path)
        => ObserveRuntimeVfxEvidence(state, path, "trigger used", RuntimeVfxEvidence.TriggerUsed);

    private ObservationApplyResult ObserveRuntimeVfxEvidence(
        CatalogAssetState state,
        string path,
        string searchTerm,
        RuntimeVfxEvidence evidence)
        => ObserveStandaloneVfxPath(
            state,
            path,
            AssetPathSource.RuntimeObserved,
            AssetPathContract.RuntimeObservation,
            [searchTerm],
            evidence,
            runtimeObserved: true);

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

    private ObservationApplyResult TryMergeAcceptedVfxEvidence(
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

        return TryPromoteStandaloneVfx(state, normalizedPath, asset.Evidence | evidence, asset.Analysis, runtimeObserved || asset.SeenFromRuntime);
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

    private sealed class RuntimeVfxAssetState(string path)
    {
        private IReadOnlyList<string>? _searchTerms;

        public string Path { get; } = path;
        public string CatalogSource { get; set; } = "vfx";
        public RuntimeVfxEvidence Evidence { get; set; }
        public bool SeenFromRuntime { get; set; }
        public VfxAnalysis? Analysis { get; set; }
        public AssetPathContract PathContracts { get; set; }
        public KnownVfxFamily FamilyHint { get; set; }
        public VfxStandaloneSupportClass SupportClass { get; set; }
        public VfxStandaloneUnsupportedReason UnsupportedReasons { get; set; }
        public VfxStandaloneContextClue ContextClues { get; set; }
        public VfxStandaloneUnknownReason UnknownReasons { get; set; }

        public string GetPrimarySource()
            => CatalogSource;

        public void InvalidateSearchTerms()
            => _searchTerms = null;

        public IReadOnlyList<string> BuildSearchTerms()
        {
            if (_searchTerms is not null)
            {
                return _searchTerms;
            }

            HashSet<string> searchTerms = ObjectSearchTermUtility.CreateSet();
            if (FamilyHint.TryGetSearchLabel() is { } familySearchTerm)
            {
                _ = ObjectSearchTermUtility.AddTerm(searchTerms, familySearchTerm);
            }

            _searchTerms = ObjectSearchTermUtility.BuildStableTerms(searchTerms);
            return _searchTerms;
        }

        public CacheSaveCapture CaptureForSave()
            => new(
                Path,
                Evidence,
                SeenFromRuntime,
                SupportClass);

        public readonly record struct CacheSaveCapture(
            string Path,
            RuntimeVfxEvidence Evidence,
            bool SeenFromRuntime,
            VfxStandaloneSupportClass SupportClass);
    }
}

