using Dalamud.Plugin.Services;
using Intoner.Objects.Assets.Cache;
using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace Intoner.Objects.Assets;

/// <summary> provides runtime discovered asset data used to populate object catalog entries </summary>
internal interface IObjectAssetIndex
{
    /// <summary> gets whether the discovery state has finished loading </summary>
    bool IsReady { get; }

    /// <summary> gets whether the discovery state is currently loading in the background </summary>
    bool IsLoading { get; }

    /// <summary> gets whether the most recent background load failed </summary>
    bool HasFailed { get; }

    /// <summary> gets the current warmup status text </summary>
    string StatusText { get; }

    /// <summary> raised when the asset index changes and catalog projections should rebuild </summary>
    event System.Action? AssetsChanged;

    /// <summary> starts background asset warmup if needed </summary>
    void EnsureWarmup();

    /// <summary> resolves cached shared group assets for a game path and loads them on demand when needed </summary>
    /// <param name="sharedGroupPath">the normalized or raw shared group path</param>
    /// <param name="sharedGroupAssets">the resolved shared group assets when available</param>
    /// <returns>true when the shared group could be resolved</returns>
    bool TryGetSharedGroupAssets(string sharedGroupPath, [NotNullWhen(true)] out SharedGroupAssetInfo? sharedGroupAssets);

    /// <summary> gets observed bgobject model assets discovered outside the built in lumina paths </summary>
    /// <param name="cancellationToken">cancels waiting for asset warmup</param>
    IReadOnlyList<ObservedBgAsset> GetObservedBgObjectAssets(CancellationToken cancellationToken = default);

    /// <summary> gets bgobject model assets resolved from static game data sources </summary>
    /// <param name="cancellationToken">cancels waiting for asset warmup</param>
    IReadOnlyList<GameDataBgObjectAsset> GetGameDataBgObjectAssets(CancellationToken cancellationToken = default);

    /// <summary> gets standalone vfx assets that currently pass the analysis policy </summary>
    /// <param name="cancellationToken">cancels waiting for asset warmup</param>
    IReadOnlyList<RuntimeVfxAsset> GetStandaloneVfxAssets(CancellationToken cancellationToken = default);

    /// <summary> evaluates standalone placement support for one vfx path </summary>
    /// <param name="vfxPath">the raw or normalized vfx path to inspect</param>
    /// <param name="report">the resulting standalone classification report when evaluation succeeds</param>
    /// <returns>true when the path resolves to a readable avfx and the classifier could evaluate it</returns>
    bool TryGetStandaloneVfxReport(string vfxPath, [NotNullWhen(true)] out VfxStandaloneReport? report);

    /// <summary> gets the current bgobject section version for catalog projection updates </summary>
    /// <param name="cancellationToken">cancels waiting for asset warmup</param>
    long GetBgObjectSectionVersion(CancellationToken cancellationToken = default);

    /// <summary> gets the current standalone vfx section version for catalog projection updates </summary>
    /// <param name="cancellationToken">cancels waiting for asset warmup</param>
    long GetStandaloneVfxSectionVersion(CancellationToken cancellationToken = default);

    /// <summary> gets explicit dependencies for one object resource path </summary>
    /// <param name="requestedPath">the original requested game path</param>
    /// <param name="effectivePath">the resolved path</param>
    /// <returns>the normalized dependency paths from the effective path</returns>
    IReadOnlyList<string> GetCollectionPathDependencies(string requestedPath, ObjectResolvedPath effectivePath);
}

internal sealed partial class ObjectAssetIndex : IObjectAssetIndex, IDisposable
{
    internal const string ObservedResourceSource = "observed resource";
    internal const string ObservedSharedGroupSource = "observed shared group";
    internal const string SqpackCollisionSource = "sqpack collision";
    internal const string SqpackSharedGroupSource = "sqpack collision shared group";

    private readonly ILogger<ObjectAssetIndex> _logger;
    private readonly IDataManager _dataManager;
    private readonly IObjectAssetGameData _gameData;
    private readonly IClientState _clientState;
    private readonly IObjectAssetCacheService _cacheService;
    private readonly IObjectAssetCacheInvalidationService _cacheInvalidationService;
    private readonly Lock _stateLock = new();
    private readonly ObjectWarmupState<CatalogAssetState> _warmupState;
    private readonly ObjectAssetObserver? _observer;
    private readonly ObjectAssetStaticDiscovery _staticDiscovery;
    private readonly bool _runtimeCaptureEnabled;
    private static readonly IComparer<ObservedBgAsset> ObservedBgAssetComparer = Comparer<ObservedBgAsset>.Create(static (left, right) =>
    {
        var sourceComparison = StringComparer.OrdinalIgnoreCase.Compare(left.Source, right.Source);
        return sourceComparison != 0
            ? sourceComparison
            : StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
    });
    private static readonly IComparer<GameDataBgObjectAsset> GameDataBgObjectAssetComparer = Comparer<GameDataBgObjectAsset>.Create(static (left, right) =>
    {
        var sourceComparison = StringComparer.OrdinalIgnoreCase.Compare(left.Source, right.Source);
        return sourceComparison != 0
            ? sourceComparison
            : StringComparer.OrdinalIgnoreCase.Compare(left.ModelPath, right.ModelPath);
    });
    private static readonly IComparer<RuntimeVfxAsset> RuntimeVfxAssetComparer = Comparer<RuntimeVfxAsset>.Create(static (left, right) =>
    {
        var sourceComparison = StringComparer.OrdinalIgnoreCase.Compare(left.Source, right.Source);
        return sourceComparison != 0
            ? sourceComparison
            : StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
    });

    private Task? _cacheSaveTask;
    private bool _cacheSaveQueued;
    private int _disposeRequested;

    public ObjectAssetIndex(
        ILogger<ObjectAssetIndex> logger,
        ILoggerFactory loggerFactory,
        IDataManager gameData,
        IClientState clientState,
        IObjectAssetCacheService cacheService,
        IObjectAssetCacheInvalidationService cacheInvalidationService,
        IObjectConfigurationService configurationService,
        IObjectAssetGameVersionService gameVersionService,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner)
    {
        _logger = logger;
        _dataManager = gameData;
        _gameData = new DalamudObjectAssetGameData(gameData);
        _clientState = clientState;
        _cacheService = cacheService;
        _cacheInvalidationService = cacheInvalidationService;
        _runtimeCaptureEnabled = configurationService.Current.AssetCapture.EnableRuntimeCapture;
        _warmupState = new ObjectWarmupState<CatalogAssetState>(
            logger,
            BuildInitialState,
            "waiting to load object assets",
            "loading object assets",
            "object assets ready",
            "object asset load failed",
            "failed to load object assets");
        if (_runtimeCaptureEnabled)
        {
            _observer = new ObjectAssetObserver(
                loggerFactory.CreateLogger<ObjectAssetObserver>(),
                gameInteropProvider,
                sigScanner,
                HandleObservationBatch);
        }

        RootExlResolver rootExlResolver = new(
            loggerFactory.CreateLogger<RootExlResolver>(),
            _gameData);
        _staticDiscovery = new ObjectAssetStaticDiscovery(
            loggerFactory.CreateLogger<ObjectAssetStaticDiscovery>(),
            _gameData,
            new SqpackIndexStore(
                loggerFactory.CreateLogger<SqpackIndexStore>(),
                gameVersionService),
            new GameDataBgObjectResolver(
                loggerFactory.CreateLogger<GameDataBgObjectResolver>(),
                _gameData),
            new GameDataVfxResolver(
                loggerFactory.CreateLogger<GameDataVfxResolver>(),
                _gameData),
            rootExlResolver,
            new RootExlVfxFamilyResolver(
                loggerFactory.CreateLogger<RootExlVfxFamilyResolver>(),
                _gameData),
            new NativeVfxFamilyResolver(
                loggerFactory.CreateLogger<NativeVfxFamilyResolver>(),
                _gameData));
        _logger.LogInformation(
            "object asset runtime capture is {State}",
            _runtimeCaptureEnabled ? "enabled" : "disabled");
    }

    public bool IsReady
        => _warmupState.IsReady;

    public bool IsLoading
        => _warmupState.IsLoading;

    public bool HasFailed
        => _warmupState.HasFailed;

    public string StatusText
        => _warmupState.StatusText;

    public event System.Action? AssetsChanged;

    public void EnsureWarmup()
        => _warmupState.EnsureWarmup();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
        {
            return;
        }

        _observer?.Dispose();
        FlushCache();
        _warmupState.Dispose();
    }

    public bool TryGetSharedGroupAssets(string sharedGroupPath, [NotNullWhen(true)] out SharedGroupAssetInfo? sharedGroupAssets)
    {
        var normalizedPath = ObjectPathRules.NormalizeGamePath(sharedGroupPath);
        if (!ObjectPathRules.IsCatalogSharedGroupPath(normalizedPath)
         || !_gameData.FileExists(normalizedPath))
        {
            sharedGroupAssets = null;
            return false;
        }

        var state = _warmupState.GetValue();
        lock (_stateLock)
        {
            if (state.SharedGroups.TryGetValue(normalizedPath, out sharedGroupAssets))
            {
                return true;
            }
        }

        sharedGroupAssets = SharedGroupAssetResolver.AnalyzeSharedGroup(_gameData, normalizedPath);
        lock (_stateLock)
        {
            if (!state.SharedGroups.TryGetValue(normalizedPath, out var cachedAssets))
            {
                state.SharedGroups[normalizedPath] = sharedGroupAssets;
            }
            else
            {
                sharedGroupAssets = cachedAssets;
            }
        }

        return true;
    }

    public IReadOnlyList<ObservedBgAsset> GetObservedBgObjectAssets(CancellationToken cancellationToken = default)
    {
        var state = _warmupState.GetValue(cancellationToken);
        lock (_stateLock)
        {
            if (!state.ObservedBgSnapshotDirty && state.ObservedBgSnapshot is not null)
            {
                return state.ObservedBgSnapshot;
            }

            ObservedBgAsset[] snapshot = BuildObservedBgSnapshot(state);
            state.ObservedBgSnapshot = snapshot;
            state.ObservedBgSnapshotDirty = false;
            return snapshot;
        }
    }

    public IReadOnlyList<GameDataBgObjectAsset> GetGameDataBgObjectAssets(CancellationToken cancellationToken = default)
    {
        var state = _warmupState.GetValue(cancellationToken);
        lock (_stateLock)
        {
            if (!state.GameDataBgSnapshotDirty && state.GameDataBgSnapshot is not null)
            {
                return state.GameDataBgSnapshot;
            }

            GameDataBgObjectAsset[] snapshot = BuildGameDataBgSnapshot(state);
            state.GameDataBgSnapshot = snapshot;
            state.GameDataBgSnapshotDirty = false;
            return snapshot;
        }
    }

    public long GetBgObjectSectionVersion(CancellationToken cancellationToken = default)
    {
        var state = _warmupState.GetValue(cancellationToken);
        lock (_stateLock)
        {
            return state.BgObjectSectionVersion;
        }
    }

    public long GetStandaloneVfxSectionVersion(CancellationToken cancellationToken = default)
    {
        var state = _warmupState.GetValue(cancellationToken);
        lock (_stateLock)
        {
            return state.StandaloneVfxSectionVersion;
        }
    }

    private CatalogAssetState BuildInitialState(CancellationToken cancellationToken)
    {
        CatalogAssetState state = new();
        string currentGameVersion = _cacheInvalidationService.CurrentGameVersion;
        string currentSqpackIndexFingerprint = _cacheInvalidationService.CurrentSqpackIndexFingerprint;
        ObjectAssetCacheSectionSet requestedSections = _runtimeCaptureEnabled
            ? ObjectAssetCacheSectionSet.All
            : ObjectAssetCacheSectionSet.AllStatic;
        ObjectAssetCacheManifest? cacheManifest = _cacheService.TryLoadManifest();
        ObjectAssetCacheSectionSet reusableSections = _cacheInvalidationService.GetReusableSections(
            cacheManifest,
            requestedSections);
        ObjectAssetCacheLoadResult cacheLoadResult = cacheManifest is not null && reusableSections != ObjectAssetCacheSectionSet.None
            ? _cacheService.Load(cacheManifest, reusableSections)
            : ObjectAssetCacheLoadResult.Empty;

        cancellationToken.ThrowIfCancellationRequested();
        if (_runtimeCaptureEnabled
         && cacheLoadResult.LoadedSections.HasAny(ObjectAssetCacheSectionSet.RuntimeOverlay))
        {
            LoadCachedRuntimeState(state, cacheLoadResult.Snapshot);
            state.DirtyCacheSections = ObjectAssetCacheSectionSet.None;
            state.CacheRevision = 0;
        }

        ObjectAssetCacheSectionSet loadedStaticSections = cacheLoadResult.LoadedSections & ObjectAssetCacheSectionSet.AllStatic;
        StaticAssetDiscoverySnapshot staticDiscoverySnapshot = ResolveStaticDiscoverySnapshot(
            currentGameVersion,
            cacheLoadResult,
            loadedStaticSections,
            cancellationToken);
        ObjectAssetCacheSectionSet overlayDirtySections = ApplyStaticDiscovery(state, staticDiscoverySnapshot, cancellationToken);
        state.SqpackIndexFingerprint = currentSqpackIndexFingerprint;
        ObjectAssetCacheSectionSet startupDirtySections = (ObjectAssetCacheSectionSet.AllStatic & ~loadedStaticSections) | overlayDirtySections;
        state.DirtyCacheSections = startupDirtySections;
        state.CacheRevision = startupDirtySections == ObjectAssetCacheSectionSet.None ? 0 : 1;

        if (startupDirtySections != ObjectAssetCacheSectionSet.None)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TrySaveCacheImmediately(
                state,
                loadedStaticSections != ObjectAssetCacheSectionSet.AllStatic
                    ? "saved object asset cache after rebuilding static discovery"
                    : "saved object asset cache after removing static overlay duplicates");
        }

        _logger.LogInformation(
            "loaded object asset state with {KnownPathCount} known paths, {BgModelCount} observed bg models and {VfxCount} standalone vfx assets",
            state.KnowledgeBase.Count,
            state.BgModels.Count,
            state.VfxAssets.Count);

        return state;
    }

    private StaticAssetDiscoverySnapshot ResolveStaticDiscoverySnapshot(
        string currentGameVersion,
        ObjectAssetCacheLoadResult cacheLoadResult,
        ObjectAssetCacheSectionSet loadedStaticSections,
        CancellationToken cancellationToken)
    {
        if (loadedStaticSections == ObjectAssetCacheSectionSet.AllStatic)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation(
                "loaded static object asset discovery snapshot from cache with {CollisionPathCount} sqpack named paths, {GameDataBgObjectCount} game data bgobject models, and {ResolvedVfxCount} resolved vfx paths",
                cacheLoadResult.Snapshot.StaticCollisionPaths.Count,
                cacheLoadResult.Snapshot.StaticGameDataBgObjects.Count,
                cacheLoadResult.Snapshot.StaticResolvedVfxEntries.Count);
            return StaticAssetDiscoverySnapshot.FromCache(currentGameVersion, cacheLoadResult.Snapshot);
        }

        StaticAssetDiscoveryReuseInput reuseInput = StaticAssetDiscoveryReuseInput.FromCache(
            cacheLoadResult.Snapshot,
            loadedStaticSections);
        return _staticDiscovery.Discover(
            currentGameVersion,
            reuseInput,
            cancellationToken);
    }

    private void HandleObservationBatch(IReadOnlyList<ObjectAssetObservation> observations)
    {
        if (observations.Count == 0 || Volatile.Read(ref _disposeRequested) != 0)
        {
            return;
        }

        CatalogAssetState state;
        try
        {
            state = _warmupState.GetValue();
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposeRequested) != 0)
        {
            return;
        }

        var stateChanged = false;
        var projectionChanged = false;

        lock (_stateLock)
        {
            if (Volatile.Read(ref _disposeRequested) != 0)
            {
                return;
            }

            foreach (var observation in observations)
            {
                var result = ApplyObservation(state, observation);
                if (result == ObservationApplyResult.None)
                {
                    continue;
                }

                stateChanged = true;
                if (result == ObservationApplyResult.ProjectionChanged)
                {
                    projectionChanged = true;
                }
            }
        }

        if (!stateChanged)
        {
            return;
        }

        if (Volatile.Read(ref _disposeRequested) != 0)
        {
            return;
        }

        ScheduleCacheSave();
        if (projectionChanged)
        {
            RaiseAssetsChanged();
        }
    }

    private ObservationApplyResult ApplyObservation(CatalogAssetState state, ObjectAssetObservation observation)
    {
        return observation.Kind switch
        {
            ObjectAssetObservationKind.ResourceLoad => ApplyResourceLoad(state, observation.Path),
            ObjectAssetObservationKind.StaticVfxCreate => ObserveStaticVfxCreate(state, observation.Path),
            ObjectAssetObservationKind.ActorVfxCreate => ObserveActorVfxCreate(state, observation.Path),
            ObjectAssetObservationKind.TriggerUse => ObserveTriggerUse(state, observation.Path),
            _ => ObservationApplyResult.None,
        };
    }

    private ObservationApplyResult ApplyResourceLoad(CatalogAssetState state, string path)
    {
        ObjectTerritoryMetadata territoryMetadata = GetCurrentTerritoryMetadata();
        if (ObjectPathRules.IsCatalogSharedGroupPath(path))
        {
            _ = AddKnowledgePath(state, path, AssetPathSource.RuntimeObserved, AssetPathContract.RuntimeObservation, [ObservedSharedGroupSource]);
            return ObserveSharedGroup(state, path, ObservedSharedGroupSource, territoryMetadata);
        }

        if (ObjectPathRules.IsCatalogModelPath(path))
        {
            return ObserveBgModelPath(
                state,
                path,
                AssetPathSource.RuntimeObserved,
                AssetPathContract.RuntimeObservation,
                [ObservedResourceSource],
                ObservedResourceSource,
                territoryMetadata);
        }

        if (ObjectPathRules.IsVfxPath(path))
        {
            return ObserveStandaloneVfxPath(
                state,
                path,
                AssetPathSource.RuntimeObserved,
                AssetPathContract.RuntimeObservation,
                ["resource load"],
                RuntimeVfxEvidence.ResourceLoad);
        }

        if (ObjectPathRules.IsCatalogTimelinePath(path))
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
        if (ObjectPathRules.IsCatalogSharedGroupPath(path))
        {
            _ = AddKnowledgePath(state, path, AssetPathSource.SqpackCollision, AssetPathContract.SqpackNamedLeak, [SqpackSharedGroupSource]);
            return ObserveSharedGroup(state, path, SqpackSharedGroupSource, ObjectTerritoryMetadata.Empty) != ObservationApplyResult.None;
        }

        if (ObjectPathRules.IsCatalogModelPath(path))
        {
            return ObserveBgModelPath(
                state,
                path,
                AssetPathSource.SqpackCollision,
                AssetPathContract.SqpackNamedLeak,
                [SqpackCollisionSource],
                SqpackCollisionSource,
                ObjectTerritoryMetadata.Empty) != ObservationApplyResult.None;
        }

        if (ObjectPathRules.IsCatalogTimelinePath(path))
        {
            _ = AddKnowledgePath(state, path, AssetPathSource.SqpackCollision, AssetPathContract.SqpackNamedLeak, ["sqpack collision", "timeline"]);
            return ApplyTimelineVfxReferences(
                state,
                path,
                AssetPathSource.SqpackCollision,
                ["sqpack collision", "timeline referenced"],
                runtimeObserved: false) != ObservationApplyResult.None;
        }

        if (ObjectPathRules.IsVfxPath(path) || ObjectPathRules.IsEidPath(path))
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
        var normalizedPath = ObjectPathRules.NormalizeGamePath(sharedGroupPath);
        if (!ObjectPathRules.IsCatalogSharedGroupPath(normalizedPath)
         || !_gameData.FileExists(normalizedPath))
        {
            return ObservationApplyResult.None;
        }

        if (!state.SharedGroups.TryGetValue(normalizedPath, out var sharedGroupAssets))
        {
            sharedGroupAssets = SharedGroupAssetResolver.AnalyzeSharedGroup(_gameData, normalizedPath);
            state.SharedGroups[normalizedPath] = sharedGroupAssets;
        }

        var result = ObservationApplyResult.None;
        foreach (var modelPath in sharedGroupAssets.BgObjectModelPaths)
        {
            result = Combine(
                result,
                ObserveBgModelPath(
                    state,
                    modelPath,
                    AssetPathSource.SharedGroup,
                    AssetPathContract.ParsedFileReference,
                    [source, "shared group"],
                    source,
                    territoryMetadata));
        }

        foreach (var vfxPath in sharedGroupAssets.StandaloneVfxPaths)
        {
            result = Combine(
                result,
                ObserveStandaloneVfxPath(
                    state,
                    vfxPath,
                    AssetPathSource.SharedGroup,
                    AssetPathContract.ParsedFileReference,
                    ["layout autoplay", "shared group"],
                    RuntimeVfxEvidence.LayoutAutoplay,
                    runtimeObserved: string.Equals(source, ObservedSharedGroupSource, StringComparison.OrdinalIgnoreCase)));
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
        var normalizedPath = ObjectPathRules.NormalizeGamePath(tmbPath);
        if (!state.ProcessedTimelinePaths.Add(normalizedPath))
        {
            return ObservationApplyResult.None;
        }

        var result = ObservationApplyResult.None;
        foreach (TmbVfxReference reference in VfxAssetAnalyzer.CollectTmbVfxReferences(_gameData, normalizedPath))
        {
            result = Combine(
                result,
                MergeTimelineVfxReference(
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

    private ObjectAssetCacheSectionSet ApplyStaticDiscovery(
        CatalogAssetState state,
        StaticAssetDiscoverySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        state.GameVersion = string.IsNullOrWhiteSpace(snapshot.GameVersion)
            ? _cacheInvalidationService.CurrentGameVersion
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

        ObjectAssetCacheSectionSet overlayDirtySections = RemoveGameDataBgDuplicates(state)
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

            ObservationApplyResult promotionResult = TryPromoteStandaloneVfx(state, resolvedVfxPath.Path, resolvedVfxPath.Evidence, resolvedVfxPath.Analysis);
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

    private void RaiseAssetsChanged()
    {
        try
        {
            AssetsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "object asset change handler failed");
        }
    }
}
