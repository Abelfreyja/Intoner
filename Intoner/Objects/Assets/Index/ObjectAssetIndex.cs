using Dalamud.Plugin.Services;
using Intoner.Objects.Assets.Cache;
using Intoner.Services.Configuration;
using Intoner.Services.Loading;
using Intoner.Objects.Resources;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Intoner.Objects.Assets;

/// <summary> provides runtime discovered asset data used to populate object catalog entries </summary>
internal interface IObjectAssetIndex : ILoadOperation
{
    /// <summary> raised when the asset index changes and catalog projections should rebuild </summary>
    event System.Action? AssetsChanged;

    /// <summary> resolves cached shared group assets for a game path and loads them on demand when needed </summary>
    /// <param name="sharedGroupPath">the normalized or raw shared group path</param>
    /// <param name="sharedGroupAssets">the resolved shared group assets when available</param>
    /// <returns>true when the shared group could be resolved</returns>
    bool TryGetSharedGroupAssets(string sharedGroupPath, [NotNullWhen(true)] out SharedGroupAssetInfo? sharedGroupAssets);

    /// <summary> gets observed bgobject model assets discovered outside the built in lumina paths </summary>
    /// <param name="cancellationToken">cancels waiting for asset loading</param>
    IReadOnlyList<ObservedBgAsset> GetObservedBgObjectAssets(CancellationToken cancellationToken = default);

    /// <summary> gets bgobject model assets resolved from static game data sources </summary>
    /// <param name="cancellationToken">cancels waiting for asset loading</param>
    IReadOnlyList<GameDataBgObjectAsset> GetGameDataBgObjectAssets(CancellationToken cancellationToken = default);

    /// <summary> gets standalone vfx assets that currently pass the analysis policy </summary>
    /// <param name="cancellationToken">cancels waiting for asset loading</param>
    IReadOnlyList<RuntimeVfxAsset> GetStandaloneVfxAssets(CancellationToken cancellationToken = default);

    /// <summary> evaluates standalone placement support for one vfx path </summary>
    /// <param name="vfxPath">the raw or normalized vfx path to inspect</param>
    /// <param name="report">the resulting standalone classification report when evaluation succeeds</param>
    /// <returns>true when the path resolves to a readable avfx and the classifier could evaluate it</returns>
    bool TryGetStandaloneVfxReport(string vfxPath, [NotNullWhen(true)] out VfxStandaloneReport? report);

    /// <summary> gets the current bgobject section version for catalog projection updates </summary>
    /// <param name="cancellationToken">cancels waiting for asset loading</param>
    long GetBgObjectSectionVersion(CancellationToken cancellationToken = default);

    /// <summary> gets the current standalone vfx section version for catalog projection updates </summary>
    /// <param name="cancellationToken">cancels waiting for asset loading</param>
    long GetStandaloneVfxSectionVersion(CancellationToken cancellationToken = default);

    /// <summary> gets explicit dependencies for one object resource path </summary>
    /// <param name="requestedPath">the original requested game path</param>
    /// <param name="effectivePath">the resolved path</param>
    /// <returns>the normalized dependency paths from the effective path</returns>
    IReadOnlyList<string> GetCollectionPathDependencies(string requestedPath, ObjectResolvedPath effectivePath);
}

internal sealed class ObjectAssetIndex : IObjectAssetIndex, IDisposable
{
    private readonly ILogger<ObjectAssetIndex> _logger;
    private readonly IObjectAssetCacheService _cacheService;
    private readonly IObjectAssetCacheInvalidationService _cacheInvalidationService;
    private readonly ObjectAssetCacheWriter _cacheWriter;
    private readonly ObjectAssetDependencyResolver _dependencyResolver;
    private readonly ObjectAssetSharedGroupCache _sharedGroupCache;
    private readonly ObjectAssetStateIngestor _stateIngestor;
    private readonly ObjectAssetStandaloneVfxCatalog _standaloneVfxCatalog;
    private readonly Lock _stateLock = new();
    private readonly BackgroundLoad<CatalogAssetState> _load;
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

    private int _disposeRequested;

    public ObjectAssetIndex(
        ILogger<ObjectAssetIndex> logger,
        ILoggerFactory loggerFactory,
        IObjectAssetCacheService cacheService,
        IObjectAssetCacheInvalidationService cacheInvalidationService,
        ObjectAssetDependencyResolver dependencyResolver,
        ObjectAssetSharedGroupCache sharedGroupCache,
        ObjectAssetStateIngestor stateIngestor,
        ObjectAssetStandaloneVfxCatalog standaloneVfxCatalog,
        IIntonerConfigurationService configurationService,
        ObjectAssetStaticDiscovery staticDiscovery,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner)
    {
        _logger = logger;
        _cacheService = cacheService;
        _cacheInvalidationService = cacheInvalidationService;
        _dependencyResolver = dependencyResolver;
        _sharedGroupCache = sharedGroupCache;
        _stateIngestor = stateIngestor;
        _standaloneVfxCatalog = standaloneVfxCatalog;
        _staticDiscovery = staticDiscovery;
        _runtimeCaptureEnabled = configurationService.Current.AssetCapture.EnableRuntimeCapture;
        _load = new BackgroundLoad<CatalogAssetState>(
            logger,
            BuildInitialState,
            new BackgroundLoadMessages(
                "loading object assets",
                "object assets ready",
                "object asset load failed",
                "failed to load object assets"));
        _cacheWriter = new ObjectAssetCacheWriter(
            loggerFactory.CreateLogger<ObjectAssetCacheWriter>(),
            cacheService,
            _stateLock,
            IsDisposed,
            TryGetLoadedState);
        if (_runtimeCaptureEnabled)
        {
            _observer = new ObjectAssetObserver(
                loggerFactory.CreateLogger<ObjectAssetObserver>(),
                gameInteropProvider,
                sigScanner,
                HandleObservationBatch);
        }
        _logger.LogInformation(
            "object asset runtime capture is {State}",
            _runtimeCaptureEnabled ? "enabled" : "disabled");
    }

    public LoadStatus Status
        => _load.Status;

    public event System.Action? AssetsChanged;

    public void EnsureLoaded()
        => _load.EnsureLoaded();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
        {
            return;
        }

        _observer?.Dispose();
        _cacheWriter.Dispose();
        _load.Dispose();
    }

    public bool TryGetSharedGroupAssets(string sharedGroupPath, [NotNullWhen(true)] out SharedGroupAssetInfo? sharedGroupAssets)
    {
        CatalogAssetState state = _load.Get();
        return _sharedGroupCache.TryGetOrAnalyzeThreadSafe(state, _stateLock, sharedGroupPath, out sharedGroupAssets);
    }

    public IReadOnlyList<string> GetCollectionPathDependencies(string requestedPath, ObjectResolvedPath effectivePath)
    {
        CatalogAssetState state = _load.Get();
        return _dependencyResolver.GetCollectionPathDependencies(
            requestedPath,
            effectivePath,
            state,
            _stateLock);
    }

    public IReadOnlyList<ObservedBgAsset> GetObservedBgObjectAssets(CancellationToken cancellationToken = default)
    {
        var state = _load.Get(cancellationToken);
        lock (_stateLock)
        {
            if (!state.ObservedBgSnapshotDirty && state.ObservedBgSnapshot is not null)
            {
                return state.ObservedBgSnapshot;
            }

            ObservedBgAsset[] snapshot = BgAssetProjection.BuildObservedSnapshot(
                state.BgModels.Values,
                state.KnowledgeBase,
                ObservedBgAssetComparer);
            state.ObservedBgSnapshot = snapshot;
            state.ObservedBgSnapshotDirty = false;
            return snapshot;
        }
    }

    public IReadOnlyList<GameDataBgObjectAsset> GetGameDataBgObjectAssets(CancellationToken cancellationToken = default)
    {
        var state = _load.Get(cancellationToken);
        lock (_stateLock)
        {
            if (!state.GameDataBgSnapshotDirty && state.GameDataBgSnapshot is not null)
            {
                return state.GameDataBgSnapshot;
            }

            GameDataBgObjectAsset[] snapshot = BgAssetProjection.BuildGameDataSnapshot(
                state.GameDataBgObjects.Values,
                GameDataBgObjectAssetComparer);
            state.GameDataBgSnapshot = snapshot;
            state.GameDataBgSnapshotDirty = false;
            return snapshot;
        }
    }

    public IReadOnlyList<RuntimeVfxAsset> GetStandaloneVfxAssets(CancellationToken cancellationToken = default)
    {
        CatalogAssetState state = _load.Get(cancellationToken);
        lock (_stateLock)
        {
            if (!state.StandaloneVfxSnapshotDirty && state.StandaloneVfxSnapshot is not null)
            {
                return state.StandaloneVfxSnapshot;
            }

            RuntimeVfxAsset[] snapshot = ObjectAssetStandaloneVfxCatalog.BuildSnapshot(state, RuntimeVfxAssetComparer);
            state.StandaloneVfxSnapshot = snapshot;
            state.StandaloneVfxSnapshotDirty = false;
            return snapshot;
        }
    }

    public bool TryGetStandaloneVfxReport(string vfxPath, [NotNullWhen(true)] out VfxStandaloneReport? report)
    {
        CatalogAssetState state = _load.Get();
        lock (_stateLock)
        {
            return _standaloneVfxCatalog.TryBuildReport(
                state,
                vfxPath,
                RuntimeVfxEvidence.None,
                analysis: null,
                runtimeObserved: false,
                out report);
        }
    }

    public long GetBgObjectSectionVersion(CancellationToken cancellationToken = default)
    {
        var state = _load.Get(cancellationToken);
        lock (_stateLock)
        {
            return state.BgObjectSectionVersion;
        }
    }

    public long GetStandaloneVfxSectionVersion(CancellationToken cancellationToken = default)
    {
        var state = _load.Get(cancellationToken);
        lock (_stateLock)
        {
            return state.StandaloneVfxSectionVersion;
        }
    }

    private CatalogAssetState BuildInitialState(LoadProgress progress)
    {
        CancellationToken cancellationToken = progress.CancellationToken;
        LoadProgressPlan progressPlan = progress.CreatePlan();
        LoadProgress cacheProgress = progressPlan.Next(25d);
        LoadProgress runtimeStateProgress = progressPlan.Next(3d);
        LoadProgress discoveryProgress = progressPlan.Next(2d);
        LoadProgress ingestionProgress = progressPlan.Next(68d);
        LoadProgress saveProgress = progressPlan.Next(2d);
        long startedAt = Stopwatch.GetTimestamp();
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
        cacheProgress.Report(1d);
        TimeSpan cacheLoadTime = Stopwatch.GetElapsedTime(startedAt);

        cancellationToken.ThrowIfCancellationRequested();
        if (_runtimeCaptureEnabled
         && cacheLoadResult.LoadedSections.HasAny(ObjectAssetCacheSectionSet.RuntimeOverlay))
        {
            _stateIngestor.LoadCachedRuntimeState(state, cacheLoadResult.Snapshot);
            state.DirtyCacheSections = ObjectAssetCacheSectionSet.None;
            state.CacheRevision = 0;
        }
        runtimeStateProgress.Report(1d);

        ObjectAssetCacheSectionSet loadedStaticSections = cacheLoadResult.LoadedSections & ObjectAssetCacheSectionSet.AllStatic;
        StaticAssetDiscoverySnapshot staticDiscoverySnapshot = ResolveStaticDiscoverySnapshot(
            currentGameVersion,
            cacheLoadResult,
            loadedStaticSections,
            cancellationToken);
        discoveryProgress.Report(1d);
        TimeSpan discoveryTime = Stopwatch.GetElapsedTime(startedAt) - cacheLoadTime;
        ObjectAssetCacheSectionSet overlayDirtySections = _stateIngestor.ApplyStaticDiscovery(
            state,
            staticDiscoverySnapshot,
            currentGameVersion,
            ingestionProgress);
        TimeSpan ingestionTime = Stopwatch.GetElapsedTime(startedAt) - cacheLoadTime - discoveryTime;
        state.SqpackIndexFingerprint = currentSqpackIndexFingerprint;
        ObjectAssetCacheSectionSet startupDirtySections = (ObjectAssetCacheSectionSet.AllStatic & ~loadedStaticSections) | overlayDirtySections;
        state.DirtyCacheSections = startupDirtySections;
        state.CacheRevision = startupDirtySections == ObjectAssetCacheSectionSet.None ? 0 : 1;

        if (startupDirtySections != ObjectAssetCacheSectionSet.None)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _cacheWriter.SaveImmediately(
                state,
                loadedStaticSections != ObjectAssetCacheSectionSet.AllStatic
                    ? "saved object asset cache after rebuilding static discovery"
                    : "saved object asset cache after removing static overlay duplicates");
        }

        saveProgress.Report(1d);

        _logger.LogInformation(
            "loaded object asset state with {KnownPathCount} known paths, {BgModelCount} observed bg models, and {VfxCount} standalone vfx assets in {ElapsedMilliseconds:F0} ms (cache {CacheMilliseconds:F0} ms, discovery {DiscoveryMilliseconds:F0} ms, ingestion {IngestionMilliseconds:F0} ms)",
            state.KnowledgeBase.Count,
            state.BgModels.Count,
            state.VfxAssets.Count,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            cacheLoadTime.TotalMilliseconds,
            discoveryTime.TotalMilliseconds,
            ingestionTime.TotalMilliseconds);

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
            state = _load.Get();
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
                ObservationApplyResult result = _stateIngestor.ApplyObservation(state, observation);
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

        _cacheWriter.Schedule();
        if (projectionChanged)
        {
            RaiseAssetsChanged();
        }
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

    private bool TryGetLoadedState([NotNullWhen(true)] out CatalogAssetState? state)
        => _load.TryGet(out state);

    private bool IsDisposed()
        => Volatile.Read(ref _disposeRequested) != 0;
}
