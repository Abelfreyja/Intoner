using Dalamud.Plugin.Services;
using Intoner.Objects.Assets.Cache;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using static Intoner.Objects.Assets.ObjectAssetStateChange;

namespace Intoner.Objects.Assets;

internal sealed class ObjectAssetStateIngestor(
    ILogger<ObjectAssetStateIngestor> logger,
    IDataManager dataManager,
    IObjectAssetGameData gameData,
    IClientState clientState,
    ObjectAssetSharedGroupCache sharedGroupCache,
    ObjectAssetStandaloneVfxCatalog standaloneVfxCatalog)
{
    private readonly ILogger<ObjectAssetStateIngestor> _logger = logger;
    private readonly IDataManager _dataManager = dataManager;
    private readonly IObjectAssetGameData _gameData = gameData;
    private readonly IClientState _clientState = clientState;
    private readonly ObjectAssetSharedGroupCache _sharedGroupCache = sharedGroupCache;
    private readonly ObjectAssetStandaloneVfxCatalog _standaloneVfxCatalog = standaloneVfxCatalog;

    public void LoadCachedRuntimeState(CatalogAssetState state, ObjectAssetCacheSnapshot snapshot)
    {
        foreach (ObjectAssetCacheBgModel bgModel in snapshot.BgModels)
        {
            if (!ObjectAssetPathRules.IsCatalogModelPath(bgModel.Path))
            {
                continue;
            }

            ObservedBgModelState asset = ObservedBgModelState.FromCache(bgModel);
            state.BgModels[asset.Path] = asset;
            _ = AddKnowledgePath(state, asset.Path, AssetPathSource.Persisted, AssetPathContract.PersistedCache, asset.Sources);
        }

        foreach (ObjectAssetCacheTimelineReferencedVfxEntry timelineReferencedVfx in snapshot.TimelineReferencedVfxEntries)
        {
            string normalizedPath = GameAssetPathRules.NormalizeGamePath(timelineReferencedVfx.Path);
            if (!GameAssetPathRules.IsFileKind(normalizedPath, GameAssetFileKind.Avfx))
            {
                continue;
            }

            _ = _standaloneVfxCatalog.MergeTimelineReference(
                state,
                normalizedPath,
                new VfxTimelineReferenceInfo(timelineReferencedVfx.Evidence, timelineReferencedVfx.Context),
                AssetPathSource.Persisted,
                AssetPathContract.PersistedCache,
                ["persisted"],
                runtimeObserved: true);
        }

        foreach (ObjectAssetCacheStandaloneVfx vfxAsset in snapshot.StandaloneVfxAssets)
        {
            string normalizedPath = GameAssetPathRules.NormalizeGamePath(vfxAsset.Path);
            if (!GameAssetPathRules.IsFileKind(normalizedPath, GameAssetFileKind.Avfx))
            {
                continue;
            }

            _ = _standaloneVfxCatalog.ObserveStandalonePath(
                state,
                normalizedPath,
                AssetPathSource.Persisted,
                AssetPathContract.PersistedCache,
                ["persisted"],
                vfxAsset.Evidence,
                runtimeObserved: true);
        }
    }

    public ObservationApplyResult ApplyObservation(CatalogAssetState state, ObjectAssetObservation observation)
        => observation.Kind switch
        {
            ObjectAssetObservationKind.ResourceLoad => ApplyResourceLoad(state, observation.Path),
            ObjectAssetObservationKind.StaticVfxCreate => _standaloneVfxCatalog.ObserveStaticCreate(state, observation.Path),
            ObjectAssetObservationKind.ActorVfxCreate => _standaloneVfxCatalog.ObserveActorCreate(state, observation.Path),
            ObjectAssetObservationKind.TriggerUse => _standaloneVfxCatalog.ObserveTriggerUse(state, observation.Path),
            _ => ObservationApplyResult.None,
        };

    public ObjectAssetCacheSectionSet ApplyStaticDiscovery(
        CatalogAssetState state,
        StaticAssetDiscoverySnapshot snapshot,
        string fallbackGameVersion,
        CancellationToken cancellationToken)
    {
        state.GameVersion = string.IsNullOrWhiteSpace(snapshot.GameVersion)
            ? fallbackGameVersion
            : snapshot.GameVersion;
        state.StaticCollisionPaths.Clear();
        state.StaticTimelineReferencedVfx.Clear();
        state.GameDataBgObjects.Clear();
        state.StaticResolvedVfxPaths.Clear();
        state.KnowledgeBase.MergeFrom(snapshot.BuildKnowledgeBase());
        int seededCollisionPathCount = 0;
        int resolvedVfxCount = 0;
        int analyzedVfxCount = 0;
        int promotedStandaloneVfxCount = 0;

        foreach (string collisionPath in snapshot.StaticCollisionPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = state.StaticCollisionPaths.Add(collisionPath);
            _ = ApplySqpackSeedPath(state, collisionPath);
            seededCollisionPathCount++;
        }

        foreach (GameDataBgObjectAsset gameDataBgObjectAsset in snapshot.StaticGameDataBgObjects.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.GameDataBgObjects[gameDataBgObjectAsset.ModelPath] = gameDataBgObjectAsset;
        }
        if (snapshot.StaticGameDataBgObjects.Count > 0)
        {
            MarkGameDataBgChanged(state);
        }

        ObjectAssetCacheSectionSet overlayDirtySections = ObjectAssetBgModelCatalog.RemoveGameDataDuplicates(state)
            ? ObjectAssetCacheSectionSet.BgModels
            : ObjectAssetCacheSectionSet.None;

        foreach (ResolvedVfxPath resolvedVfxPath in snapshot.StaticResolvedVfxPaths.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hadRuntimeObservedStandalone = state.VfxAssets.TryGetValue(resolvedVfxPath.Path, out RuntimeVfxAssetState? existingStandalone)
                && existingStandalone.SeenFromRuntime;
            state.StaticResolvedVfxPaths[resolvedVfxPath.Path] = resolvedVfxPath;
            resolvedVfxCount++;
            if (resolvedVfxPath.Analysis is not null)
            {
                analyzedVfxCount++;
            }

            ObservationApplyResult promotionResult = _standaloneVfxCatalog.TryPromote(state, resolvedVfxPath.Path, resolvedVfxPath.Evidence, resolvedVfxPath.Analysis);
            if (promotionResult == ObservationApplyResult.ProjectionChanged)
            {
                promotedStandaloneVfxCount++;
            }

            bool hasRuntimeObservedStandalone = state.VfxAssets.TryGetValue(resolvedVfxPath.Path, out RuntimeVfxAssetState? promotedStandalone)
                && promotedStandalone.SeenFromRuntime;
            if (promotionResult != ObservationApplyResult.None
             && (hadRuntimeObservedStandalone || hasRuntimeObservedStandalone))
            {
                overlayDirtySections |= ObjectAssetCacheSectionSet.StandaloneVfx;
            }
        }

        _logger.LogInformation(
            "applied static asset discovery with {SeededCollisionPathCount} collision seed paths, {ResolvedVfxCount} resolved vfx paths, {AnalyzedVfxCount} analyzed standalone vfx paths, and {PromotedStandaloneVfxCount} promoted standalone vfx assets",
            seededCollisionPathCount,
            resolvedVfxCount,
            analyzedVfxCount,
            promotedStandaloneVfxCount);

        if (RemoveStaticTimelineReferencedDuplicates(state))
        {
            overlayDirtySections |= ObjectAssetCacheSectionSet.TimelineReferencedVfx;
        }

        return overlayDirtySections;
    }

    private ObservationApplyResult ApplyResourceLoad(CatalogAssetState state, string path)
    {
        ObjectTerritoryMetadata territoryMetadata = GetCurrentTerritoryMetadata();
        if (ObjectAssetPathRules.IsCatalogSharedGroupPath(path))
        {
            _ = AddKnowledgePath(state, path, AssetPathSource.RuntimeObserved, AssetPathContract.RuntimeObservation, [ObjectAssetCaptureSources.ObservedSharedGroup]);
            return ObserveSharedGroup(state, path, ObjectAssetCaptureSources.ObservedSharedGroup, territoryMetadata);
        }

        if (ObjectAssetPathRules.IsCatalogModelPath(path))
        {
            return ObjectAssetBgModelCatalog.ObservePath(
                state,
                path,
                AssetPathSource.RuntimeObserved,
                AssetPathContract.RuntimeObservation,
                [ObjectAssetCaptureSources.ObservedResource],
                ObjectAssetCaptureSources.ObservedResource,
                territoryMetadata);
        }

        if (GameAssetPathRules.IsFileKind(path, GameAssetFileKind.Avfx))
        {
            return _standaloneVfxCatalog.ObserveStandalonePath(
                state,
                path,
                AssetPathSource.RuntimeObserved,
                AssetPathContract.RuntimeObservation,
                ["resource load"],
                RuntimeVfxEvidence.ResourceLoad,
                runtimeObserved: true);
        }

        if (GameAssetPathRules.IsFileKind(path, GameAssetFileKind.Tmb))
        {
            _ = AddKnowledgePath(state, path, AssetPathSource.RuntimeObserved, AssetPathContract.RuntimeObservation, ["timeline"]);
            return ApplyTimelineVfxReferences(
                state,
                path,
                AssetPathSource.RuntimeObserved,
                ["timeline referenced"],
                runtimeObserved: true);
        }

        return ObservationApplyResult.None;
    }

    private bool ApplySqpackSeedPath(CatalogAssetState state, string path)
    {
        if (ObjectAssetPathRules.IsCatalogSharedGroupPath(path))
        {
            _ = AddKnowledgePath(state, path, AssetPathSource.SqpackCollision, AssetPathContract.SqpackNamedLeak, [ObjectAssetCaptureSources.SqpackSharedGroup]);
            return ObserveSharedGroup(state, path, ObjectAssetCaptureSources.SqpackSharedGroup, ObjectTerritoryMetadata.Empty) != ObservationApplyResult.None;
        }

        if (ObjectAssetPathRules.IsCatalogModelPath(path))
        {
            return ObjectAssetBgModelCatalog.ObservePath(
                state,
                path,
                AssetPathSource.SqpackCollision,
                AssetPathContract.SqpackNamedLeak,
                [ObjectAssetCaptureSources.SqpackCollision],
                ObjectAssetCaptureSources.SqpackCollision,
                ObjectTerritoryMetadata.Empty) != ObservationApplyResult.None;
        }

        if (GameAssetPathRules.IsFileKind(path, GameAssetFileKind.Tmb))
        {
            _ = AddKnowledgePath(state, path, AssetPathSource.SqpackCollision, AssetPathContract.SqpackNamedLeak, ["sqpack collision", "timeline"]);
            return ApplyTimelineVfxReferences(
                state,
                path,
                AssetPathSource.SqpackCollision,
                ["sqpack collision", "timeline referenced"],
                runtimeObserved: false) != ObservationApplyResult.None;
        }

        if (GameAssetPathRules.IsFileKind(path, GameAssetFileKind.Avfx) || GameAssetPathRules.IsFileKind(path, GameAssetFileKind.Eid))
        {
            return AddKnowledgePath(state, path, AssetPathSource.SqpackCollision, AssetPathContract.SqpackNamedLeak, ["sqpack collision"]);
        }

        return false;
    }

    private ObservationApplyResult ObserveSharedGroup(
        CatalogAssetState state,
        string sharedGroupPath,
        string source,
        in ObjectTerritoryMetadata territoryMetadata)
    {
        if (!_sharedGroupCache.TryGetOrAnalyzeFromState(state, sharedGroupPath, out SharedGroupAssetInfo? sharedGroupAssets))
        {
            return ObservationApplyResult.None;
        }

        ObservationApplyResult result = ObservationApplyResult.None;
        foreach (string modelPath in sharedGroupAssets.BgObjectModelPaths)
        {
            result = Combine(
                result,
                ObjectAssetBgModelCatalog.ObservePath(
                    state,
                    modelPath,
                    AssetPathSource.SharedGroup,
                    AssetPathContract.ParsedFileReference,
                    [source, "shared group"],
                    source,
                    territoryMetadata));
        }

        bool runtimeObserved = string.Equals(source, ObjectAssetCaptureSources.ObservedSharedGroup, StringComparison.OrdinalIgnoreCase);
        bool addedReferencedVfx = false;
        foreach (string vfxPath in sharedGroupAssets.ReferencedVfxPaths)
        {
            addedReferencedVfx |= AddKnowledgePath(
                state,
                vfxPath,
                AssetPathSource.SharedGroup,
                AssetPathContract.ParsedFileReference,
                ["shared group vfx", "shared group"]);
        }

        if (addedReferencedVfx)
        {
            result = Combine(result, ObservationApplyResult.MetadataChanged);
        }

        foreach (string vfxPath in sharedGroupAssets.StandaloneVfxPaths)
        {
            result = Combine(
                result,
                _standaloneVfxCatalog.ObserveStandalonePath(
                    state,
                    vfxPath,
                    AssetPathSource.SharedGroup,
                    AssetPathContract.ParsedFileReference,
                    ["shared group autoplay", "shared group"],
                    RuntimeVfxEvidence.LayoutAutoplay,
                    runtimeObserved));
        }

        return result;
    }

    private ObservationApplyResult ApplyTimelineVfxReferences(
        CatalogAssetState state,
        string tmbPath,
        AssetPathSource source,
        IReadOnlyList<string> searchTerms,
        bool runtimeObserved)
    {
        string normalizedPath = GameAssetPathRules.NormalizeGamePath(tmbPath);
        if (!state.ProcessedTimelinePaths.Add(normalizedPath))
        {
            return ObservationApplyResult.None;
        }

        ObservationApplyResult result = ObservationApplyResult.None;
        foreach (TmbVfxReference reference in VfxAssetAnalyzer.CollectTmbVfxReferences(_gameData, normalizedPath))
        {
            result = Combine(
                result,
                _standaloneVfxCatalog.MergeTimelineReference(
                    state,
                    reference.Path,
                    new VfxTimelineReferenceInfo(reference.Evidence, reference.ContextFlags),
                    source,
                    AssetPathContract.ParsedFileReference,
                    ObjectSearchTermUtility.MergeTerms(searchTerms, reference.SearchTerms),
                    runtimeObserved));
        }

        return result;
    }

    private static bool RemoveStaticTimelineReferencedDuplicates(CatalogAssetState state)
    {
        if (state.StaticTimelineReferencedVfx.Count == 0 || state.RuntimeTimelineReferencedVfx.Count == 0)
        {
            return false;
        }

        bool removedAny = false;
        foreach (string path in state.StaticTimelineReferencedVfx.Keys)
        {
            removedAny |= state.RuntimeTimelineReferencedVfx.Remove(path);
        }

        if (removedAny)
        {
            MarkCacheSectionsDirty(state, ObjectAssetCacheSectionSet.TimelineReferencedVfx);
        }

        return removedAny;
    }

    private ObjectTerritoryMetadata GetCurrentTerritoryMetadata()
        => ObjectTerritoryMetadataUtility.BuildForTerritoryId(_clientState.TerritoryType, _dataManager);
}
