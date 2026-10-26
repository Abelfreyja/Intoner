using Intoner.Logging;
using Intoner.Objects.Assets;
using Intoner.Objects.Resources;
using Intoner.Services.Loading;
using Intoner.Utils;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Intoner.Objects.Catalog;

/// <summary>
/// Provides cached object catalog data and preview model resolution.
/// </summary>
internal interface IObjectCatalogService : ILoadOperation
{
    /// <summary>
    /// Gets the cached object catalog if it is already ready.
    /// </summary>
    /// <param name="catalog">The ready catalog when available.</param>
    /// <returns>true when the catalog is already ready.</returns>
    bool TryGetCatalog([NotNullWhen(true)] out ObjectCatalogData? catalog);

    /// <summary>
    /// Gets the cached object catalog, blocking until it is ready if needed.
    /// </summary>
    /// <returns>The current object catalog.</returns>
    ObjectCatalogData GetCatalog();

    /// <summary>
    /// Resolves preview models for the given catalog entry path.
    /// </summary>
    /// <param name="kind">The catalog kind to resolve.</param>
    /// <param name="path">The source asset path from the catalog entry.</param>
    /// <returns>The previewable models for that entry.</returns>
    IReadOnlyList<PreviewModelInfo> ResolvePreviewModels(ObjectCatalogKind kind, string path);

    /// <summary>
    /// Resolves a catalog entry by kind and placement path.
    /// </summary>
    /// <param name="kind">the catalog kind to resolve.</param>
    /// <param name="path">the placement path from the catalog entry.</param>
    /// <param name="entry">the resolved entry when available.</param>
    /// <returns>true when the entry exists in the ready catalog.</returns>
    bool TryResolveEntry(
        ObjectCatalogKind kind,
        string path,
        [NotNullWhen(true)] out ObjectCatalogEntry? entry);

    /// <summary>
    /// Resolves the exact furniture catalog variant for a sgb path and row identity.
    /// </summary>
    /// <param name="sharedGroupPath">the furniture sgb path.</param>
    /// <param name="housingRowId">the housing row id.</param>
    /// <param name="itemRowId">the item row id when available.</param>
    /// <param name="entry">the resolved catalog entry.</param>
    /// <param name="variant">the resolved furniture variant.</param>
    /// <returns>true when the path and supplied row identity resolve to the same variant.</returns>
    bool TryResolveFurnitureVariant(
        string sharedGroupPath,
        uint housingRowId,
        uint itemRowId,
        [NotNullWhen(true)] out ObjectCatalogEntry? entry,
        [NotNullWhen(true)] out ObjectCatalogFurnitureVariant? variant);
}

internal sealed class ObjectCatalogService : IObjectCatalogService, IDisposable
{
    private readonly ILogger<ObjectCatalogService> _logger;
    private readonly BackgroundLoad<ObjectCatalogData> _load;
    private readonly LoadGroup _loading;
    private readonly Lock _updateLock = new();
    private readonly IObjectAssetIndex _assetIndex;
    private readonly ObjectCatalogBuilder _builder;
    private readonly DisposalState _disposeState = new();

    private Task? _projectionUpdateTask;
    private CancellationTokenSource? _projectionUpdateCancellation;
    private bool _projectionUpdateRequested;
    private long _appliedBgObjectSectionVersion = -1;
    private long _appliedStandaloneVfxSectionVersion = -1;
    private readonly Dictionary<string, ObjectCatalogEntry> _pendingFurniturePreviews = new(StringComparer.OrdinalIgnoreCase);
    private long _nextFurniturePreviewRetryAtMs;

    public ObjectCatalogService(
        ILogger<ObjectCatalogService> logger,
        ObjectCatalogBuilder builder,
        IObjectAssetIndex assetIndex)
    {
        _logger = logger;
        _assetIndex = assetIndex;
        _builder = builder;
        _load = new BackgroundLoad<ObjectCatalogData>(
            logger,
            BuildCatalog,
            new BackgroundLoadMessages(
                "building object catalog",
                "object catalog ready",
                "object catalog load failed",
                "failed to build object catalog in background"));
        _loading = new LoadGroup(
            (_assetIndex, 35d),
            (_load, 65d));
        _assetIndex.AssetsChanged += HandleCatalogAssetsChanged;
    }

    public LoadStatus Status
        => _loading.Status;

    public void EnsureLoaded()
        => _loading.EnsureLoaded();

    public bool TryGetCatalog([NotNullWhen(true)] out ObjectCatalogData? catalog)
    {
        if (!_load.TryGet(out catalog))
        {
            return false;
        }

        ScheduleFurniturePreviewRetry();
        return true;
    }

    public ObjectCatalogData GetCatalog()
    {
        EnsureLoaded();
        ObjectCatalogData catalog = _load.Get();
        ScheduleFurniturePreviewRetry();
        return catalog;
    }

    public IReadOnlyList<PreviewModelInfo> ResolvePreviewModels(ObjectCatalogKind kind, string path)
    {
        if (!TryNormalizeCatalogPath(path, out string normalizedPath))
        {
            return [];
        }

        return GetCatalog().ResolvePreviewModels(kind, normalizedPath);
    }

    public bool TryResolveEntry(
        ObjectCatalogKind kind,
        string path,
        [NotNullWhen(true)] out ObjectCatalogEntry? entry)
    {
        if (!TryNormalizeCatalogPath(path, out string normalizedPath))
        {
            entry = null;
            return false;
        }

        return GetCatalog().TryResolveEntry(kind, normalizedPath, out entry);
    }

    public bool TryResolveFurnitureVariant(
        string sharedGroupPath,
        uint housingRowId,
        uint itemRowId,
        [NotNullWhen(true)] out ObjectCatalogEntry? entry,
        [NotNullWhen(true)] out ObjectCatalogFurnitureVariant? variant)
    {
        if (!TryNormalizeCatalogPath(sharedGroupPath, out string normalizedPath))
        {
            entry = null;
            variant = null;
            return false;
        }

        return GetCatalog().TryResolveFurnitureVariant(
            normalizedPath,
            housingRowId,
            itemRowId,
            out entry,
            out variant);
    }

    private static bool TryNormalizeCatalogPath(string path, out string normalizedPath)
    {
        normalizedPath = ObjectResolvedPath.FromGamePath(path).Path;
        return normalizedPath.Length > 0;
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose())
        {
            return;
        }

        _assetIndex.AssetsChanged -= HandleCatalogAssetsChanged;
        Task? projectionUpdateTask;
        CancellationTokenSource? projectionUpdateCancellation;
        lock (_updateLock)
        {
            _projectionUpdateRequested = false;
            projectionUpdateTask = _projectionUpdateTask;
            projectionUpdateCancellation = _projectionUpdateCancellation;
            _projectionUpdateTask = null;
            _projectionUpdateCancellation = null;
            _pendingFurniturePreviews.Clear();
            Volatile.Write(ref _nextFurniturePreviewRetryAtMs, 0);
        }

        projectionUpdateCancellation?.Cancel();
        _load.Dispose();
        WaitForProjectionUpdate(projectionUpdateTask);
        projectionUpdateCancellation?.Dispose();
    }

    private ObjectCatalogData BuildCatalog(LoadProgress progress)
    {
        CancellationToken cancellationToken = progress.CancellationToken;
        long startedAt = Stopwatch.GetTimestamp();

        cancellationToken.ThrowIfCancellationRequested();
        ObjectAssetCatalogSnapshot assets = _assetIndex.GetCatalogSnapshot(cancellationToken);
        TimeSpan assetWaitTime = Stopwatch.GetElapsedTime(startedAt);
        ObjectCatalogData catalog = _builder.Build(assets, progress);
        TimeSpan totalTime = Stopwatch.GetElapsedTime(startedAt);
        lock (_updateLock)
        {
            _appliedBgObjectSectionVersion = assets.BgObjectVersion;
            _appliedStandaloneVfxSectionVersion = assets.StandaloneVfxVersion;
            InitializeFurniturePreviewRetries(catalog);
        }

        _logger.LogInformation(
            "built object catalog with {TotalCount} entries, {FurnitureCount} furniture entries, {BgObjectCount} bgobject entries, and {VfxCount} vfx entries in {ElapsedMilliseconds:F0} ms after waiting {AssetWaitMilliseconds:F0} ms for assets",
            catalog.EntryCount,
            catalog.Furniture.Count,
            catalog.BgObjects.Count,
            catalog.Vfx.Count,
            totalTime.TotalMilliseconds,
            assetWaitTime.TotalMilliseconds);

        return catalog;
    }

    private void InitializeFurniturePreviewRetries(ObjectCatalogData catalog)
    {
        if (IsDisposing)
        {
            return;
        }

        foreach (ObjectCatalogEntry entry in catalog.Furniture.Entries)
        {
            if (!entry.PreviewModelDiscoveryComplete)
            {
                _pendingFurniturePreviews.Add(entry.PlacementPath, entry);
            }
        }

        Volatile.Write(ref _nextFurniturePreviewRetryAtMs, _pendingFurniturePreviews.Count == 0
            ? 0 : Environment.TickCount64 + (long)ObjectAssetFileUtility.AssetReadRetryDelay.TotalMilliseconds);
    }

    private void ScheduleFurniturePreviewRetry()
    {
        long retryAtMs = Volatile.Read(ref _nextFurniturePreviewRetryAtMs);
        if (retryAtMs > 0 && Environment.TickCount64 >= retryAtMs)
        {
            ScheduleProjectionUpdate();
        }
    }

    private void HandleCatalogAssetsChanged()
    {
        ScheduleProjectionUpdate();
    }

    private void ScheduleProjectionUpdate()
    {
        if (IsDisposing)
        {
            return;
        }

        lock (_updateLock)
        {
            if (IsDisposing)
            {
                return;
            }

            _projectionUpdateRequested = true;
            if (_projectionUpdateTask is { IsCompleted: false })
            {
                return;
            }

            _projectionUpdateCancellation?.Dispose();
            _projectionUpdateCancellation = new CancellationTokenSource();
            CancellationToken token = _projectionUpdateCancellation.Token;
            _projectionUpdateTask = Task.Run(() => ProcessProjectionUpdates(token), token);
        }
    }

    private void ProcessProjectionUpdates(CancellationToken cancellationToken)
    {
        try
        {
            while (!IsDisposing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_updateLock)
                {
                    if (!_projectionUpdateRequested)
                    {
                        ClearProjectionUpdateTaskLocked();
                        return;
                    }

                    _projectionUpdateRequested = false;
                }

                try
                {
                    ApplyProjectionUpdate(_load.Get(cancellationToken), cancellationToken);
                }
                catch (ObjectDisposedException) when (IsDisposing)
                {
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (!IsDisposing)
                    {
                        _logger.LogWarning(ex, "failed to update object catalog projection");
                    }
                }

                lock (_updateLock)
                {
                    if (!_projectionUpdateRequested)
                    {
                        ClearProjectionUpdateTaskLocked();
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // cancellation is expected during disposal
        }

        lock (_updateLock)
        {
            ClearProjectionUpdateTaskLocked();
        }
    }

    private void ApplyProjectionUpdate(ObjectCatalogData catalog, CancellationToken cancellationToken)
    {
        if (IsDisposing)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<ObjectCatalogEntry>? bgObjectEntries = null;
        IReadOnlyList<ObjectCatalogEntry>? vfxEntries = null;
        ObjectAssetCatalogSnapshot assets = _assetIndex.GetCatalogSnapshot(cancellationToken);

        if (assets.BgObjectVersion != _appliedBgObjectSectionVersion)
        {
            bgObjectEntries = ObjectCatalogBuilder.BuildBgObjectEntries(assets.BgObjects, LoadProgress.Unreported(cancellationToken));
        }

        if (assets.StandaloneVfxVersion != _appliedStandaloneVfxSectionVersion)
        {
            vfxEntries = ObjectCatalogBuilder.BuildVfxEntries(assets.StandaloneVfx, LoadProgress.Unreported(cancellationToken));
        }

        Dictionary<string, ObjectCatalogEntry>? furnitureUpdates = BuildFurniturePreviewUpdates(cancellationToken);
        if (bgObjectEntries is null && vfxEntries is null && furnitureUpdates is null)
        {
            return;
        }

        ObjectCatalogEntry[]? furnitureEntries = furnitureUpdates is null ? null : catalog.Furniture.Entries
            .Select(entry => furnitureUpdates.GetValueOrDefault(entry.PlacementPath, entry))
            .ToArray();

        cancellationToken.ThrowIfCancellationRequested();
        if (IsDisposing)
        {
            return;
        }

        catalog.ReplaceSections(
            bgObjectEntries: bgObjectEntries,
            furnitureEntries: furnitureEntries,
            vfxEntries: vfxEntries,
            cancellationToken: cancellationToken);

        lock (_updateLock)
        {
            if (IsDisposing)
            {
                return;
            }

            if (furnitureUpdates is not null)
            {
                foreach (string path in furnitureUpdates.Keys)
                {
                    _pendingFurniturePreviews.Remove(path);
                }

                if (_pendingFurniturePreviews.Count == 0)
                {
                    Volatile.Write(ref _nextFurniturePreviewRetryAtMs, 0);
                }
            }

            if (bgObjectEntries is not null)
            {
                _appliedBgObjectSectionVersion = assets.BgObjectVersion;
            }

            if (vfxEntries is not null)
            {
                _appliedStandaloneVfxSectionVersion = assets.StandaloneVfxVersion;
            }
        }
    }

    private Dictionary<string, ObjectCatalogEntry>? BuildFurniturePreviewUpdates(CancellationToken cancellationToken)
    {
        ObjectCatalogEntry[] pending;
        long now = Environment.TickCount64;
        lock (_updateLock)
        {
            if (IsDisposing || _nextFurniturePreviewRetryAtMs == 0 || now < _nextFurniturePreviewRetryAtMs)
            {
                return null;
            }

            pending = _pendingFurniturePreviews.Values.ToArray();
            Volatile.Write(ref _nextFurniturePreviewRetryAtMs,
                now + (long)ObjectAssetFileUtility.AssetReadRetryDelay.TotalMilliseconds);
        }

        Dictionary<string, ObjectCatalogEntry>? updates = null;
        foreach (ObjectCatalogEntry entry in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!_assetIndex.TryGetSharedGroupAssets(entry.PlacementPath, out SharedGroupAssetInfo? group)
                 || !group.IsComplete)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                updates ??= new Dictionary<string, ObjectCatalogEntry>(StringComparer.OrdinalIgnoreCase);
                updates.Add(entry.PlacementPath, entry.WithCompletedFurniturePreview(group));
            }
            catch (Exception ex) when (ObjectAssetFileUtility.IsReadFailure(ex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DiagnosticLog.Write(_logger, LogLevel.Warning, ex, "failed to refresh furniture preview models for {Path}", entry.PlacementPath);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return updates;
    }

    private bool IsDisposing
        => _disposeState.IsDisposing;

    private void ClearProjectionUpdateTaskLocked()
    {
        _projectionUpdateTask = null;
        _projectionUpdateCancellation?.Dispose();
        _projectionUpdateCancellation = null;
    }

    private void WaitForProjectionUpdate(Task? projectionUpdateTask)
    {
        try
        {
            projectionUpdateTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // cancellation is expected during disposal
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to stop object catalog projection update");
        }
    }
}
