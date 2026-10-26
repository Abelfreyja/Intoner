using Dalamud.Plugin.Services;
using Intoner.Objects.Assets.Cache;
using Intoner.Objects.Utils;
using Intoner.Services.Configuration;
using Intoner.Services.Loading;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Intoner.Objects.Assets;

/// <summary> catalog assets captured with their section versions </summary>
internal readonly record struct ObjectAssetCatalogSnapshot(
    IReadOnlyList<BgObjectAsset> BgObjects,
    long BgObjectVersion,
    IReadOnlyList<RuntimeVfxAsset> StandaloneVfx,
    long StandaloneVfxVersion);

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

    /// <summary> captures bgobject and standalone vfx assets with their current section versions </summary>
    /// <param name="cancellationToken">cancels asset loading and snapshot capture</param>
    /// <returns>the assets and their versions captured together</returns>
    ObjectAssetCatalogSnapshot GetCatalogSnapshot(CancellationToken cancellationToken = default);

    /// <summary> evaluates standalone placement support for one vfx path </summary>
    /// <param name="vfxPath">the raw or normalized vfx path to inspect</param>
    /// <param name="report">the resulting standalone classification report when evaluation succeeds</param>
    /// <returns>true when the path resolves to a readable avfx and the classifier could evaluate it</returns>
    bool TryGetStandaloneVfxReport(string vfxPath, [NotNullWhen(true)] out VfxStandaloneReport? report);
}

internal sealed class ObjectAssetIndex : IObjectAssetIndex, IDisposable
{
    private readonly ILogger<ObjectAssetIndex> _logger;
    private readonly IObjectAssetCacheService _cacheService;
    private readonly IObjectAssetCacheInvalidationService _cacheInvalidationService;
    private readonly ObjectAssetCacheWriter _cacheWriter;
    private readonly ObjectAssetSharedGroupCache _sharedGroupCache;
    private readonly ObjectAssetStateIngestor _stateIngestor;
    private readonly ObjectAssetStandaloneVfxCatalog _standaloneVfxCatalog;
    private readonly Lock _stateLock = new();
    private readonly BackgroundLoad<CatalogAssetState> _load;
    private readonly ObjectAssetObserver? _observer;
    private readonly ObjectAssetStaticDiscovery _staticDiscovery;
    private readonly bool _runtimeCaptureEnabled;
    private static readonly IComparer<BgObjectAsset> BgObjectAssetComparer = Comparer<BgObjectAsset>.Create(static (left, right) =>
    {
        int sourceComparison = StringComparer.OrdinalIgnoreCase.Compare(left.Source, right.Source);
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
        sharedGroupAssets = _sharedGroupCache.GetOrAnalyzeThreadSafe(state.SharedGroups, _stateLock, sharedGroupPath);
        return sharedGroupAssets is { Exists: true };
    }

    public ObjectAssetCatalogSnapshot GetCatalogSnapshot(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CatalogAssetState state = _load.Get(cancellationToken);

        lock (_stateLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BgObjectAsset[] bgObjects = !state.BgObjectSnapshotDirty && state.BgObjectSnapshot is not null
                ? state.BgObjectSnapshot
                : BgAssetProjection.BuildSnapshot(state, BgObjectAssetComparer);
            RuntimeVfxAsset[] standaloneVfx = !state.StandaloneVfxSnapshotDirty && state.StandaloneVfxSnapshot is not null
                ? state.StandaloneVfxSnapshot
                : ObjectAssetStandaloneVfxCatalog.BuildSnapshot(state, RuntimeVfxAssetComparer);
            cancellationToken.ThrowIfCancellationRequested();
            state.BgObjectSnapshot = bgObjects;
            state.StandaloneVfxSnapshot = standaloneVfx;
            state.BgObjectSnapshotDirty = false;
            state.StandaloneVfxSnapshotDirty = false;
            return new(bgObjects, state.BgObjectSectionVersion, standaloneVfx, state.StandaloneVfxSectionVersion);
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
        ObjectAssetCacheSectionSet requestedSections = _runtimeCaptureEnabled
            ? ObjectAssetCacheSectionSet.All
            : ObjectAssetCacheSectionSet.AllStatic;
        ObjectAssetCacheLoadRequest cacheRequest = _cacheInvalidationService.CreateLoadRequest(requestedSections);
        CatalogAssetState state = new()
        {
            Language = cacheRequest.Language,
        };
        string currentGameVersion = cacheRequest.GameVersion ?? string.Empty;
        string currentSqpackIndexFingerprint = cacheRequest.SqpackIndexFingerprint ?? string.Empty;
        ObjectAssetCacheLoadResult cacheLoadResult = _cacheService.Load(cacheRequest);
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
        ObjectAssetCacheSectionSet rebuiltStaticSections = staticDiscoverySnapshot.CompleteSections & ~loadedStaticSections;
        ObjectAssetCacheSectionSet startupDirtySections = rebuiltStaticSections | overlayDirtySections;
        state.DirtyCacheSections = startupDirtySections;
        state.CacheRevision = startupDirtySections == ObjectAssetCacheSectionSet.None ? 0 : 1;

        if (startupDirtySections != ObjectAssetCacheSectionSet.None)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _cacheWriter.SaveImmediately(
                state,
                loadedStaticSections != ObjectAssetCacheSectionSet.AllStatic
                    ? "saved object asset cache after rebuilding static discovery"
                    : "saved object asset cache after updating runtime metadata");
        }

        saveProgress.Report(1d);

        _logger.LogInformation(
            "loaded object asset state with {KnownPathCount} known paths, {BgModelCount} observed bg models, and {VfxCount} standalone vfx assets in {ElapsedMilliseconds:F0} ms (cache {CacheMilliseconds:F0} ms, discovery {DiscoveryMilliseconds:F0} ms, ingestion {IngestionMilliseconds:F0} ms)",
            state.KnowledgeBase.Count,
            state.BgModels.Count,
            state.VfxAssets.Values.Count(static asset => asset.Report?.IsSupportedStandalone == true),
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

        ObjectTerritoryMetadata territoryMetadata;
        try
        {
            territoryMetadata = _stateIngestor.CaptureObservationTerritory(observations);
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _disposeRequested) != 0)
            {
                return;
            }

            _logger.LogWarning(ex, "failed to read asset observation territory");
            territoryMetadata = ObjectTerritoryMetadata.Empty;
        }

        var stateChanged = false;
        var projectionChanged = false;

        lock (_stateLock)
        {
            if (Volatile.Read(ref _disposeRequested) != 0)
            {
                return;
            }

            long cacheRevision = state.CacheRevision;
            long bgVersion = state.BgObjectSectionVersion;
            long vfxVersion = state.StandaloneVfxSectionVersion;
            foreach (ObjectAssetObservation observation in observations)
            {
                try
                {
                    ObservationApplyResult result = _stateIngestor.ApplyObservation(state, observation, territoryMetadata);
                    stateChanged |= result != ObservationApplyResult.None;
                    projectionChanged |= result == ObservationApplyResult.ProjectionChanged;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "failed to observe object asset {Path}", observation.Path);
                }
            }

            stateChanged |= state.CacheRevision != cacheRevision;
            projectionChanged |= state.BgObjectSectionVersion != bgVersion
                || state.StandaloneVfxSectionVersion != vfxVersion;
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
