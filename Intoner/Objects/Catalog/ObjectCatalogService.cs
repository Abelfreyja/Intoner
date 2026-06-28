using Dalamud.Plugin.Services;
using Intoner.Objects.Assets;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace Intoner.Objects.Catalog;

/// <summary>
/// Provides cached object catalog data and preview model resolution.
/// </summary>
internal interface IObjectCatalogService
{
    /// <summary>
    /// Gets whether the catalog has finished loading.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Gets whether the catalog is currently loading in the background.
    /// </summary>
    bool IsLoading { get; }

    /// <summary>
    /// Gets whether the most recent background load failed.
    /// </summary>
    bool HasFailed { get; }

    /// <summary>
    /// Gets the current warmup status text.
    /// </summary>
    string StatusText { get; }

    /// <summary>
    /// Starts background catalog warmup if needed.
    /// </summary>
    void EnsureWarmup();

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
    /// Resolves preview model paths for the given catalog entry path.
    /// </summary>
    /// <param name="kind">The catalog kind to resolve.</param>
    /// <param name="path">The source asset path from the catalog entry.</param>
    /// <returns>The previewable model paths for that entry.</returns>
    IReadOnlyList<string> ResolvePreviewModelPaths(ObjectCatalogKind kind, string path);

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
    /// Resolves raw housing furniture metadata for a sgb path.
    /// </summary>
    /// <param name="sharedGroupPath">the furniture sgb path.</param>
    /// <param name="housingRowId">the exact housing row id when known.</param>
    /// <param name="itemRowId">the exact item row id when known.</param>
    /// <param name="metadata">the resolved housing metadata when available.</param>
    /// <returns>true when the sgb path maps to a furniture catalog entry.</returns>
    bool TryResolveFurnitureMetadata(
        string sharedGroupPath,
        uint housingRowId,
        uint itemRowId,
        [NotNullWhen(true)] out HousingFurnitureMetadata? metadata);
}

internal sealed class ObjectCatalogService : IObjectCatalogService, IDisposable
{
    private readonly ILogger<ObjectCatalogService> _logger;
    private readonly ObjectWarmupState<ObjectCatalogData> _warmupState;
    private readonly Lock _updateLock = new();
    private readonly IObjectAssetIndex _assetIndex;
    private readonly ObjectCatalogBuilder _builder;
    private readonly ObjectDisposalState _disposeState = new();

    private Task? _projectionUpdateTask;
    private CancellationTokenSource? _projectionUpdateCancellation;
    private bool _projectionUpdateRequested;
    private long _appliedBgObjectSectionVersion = -1;
    private long _appliedStandaloneVfxSectionVersion = -1;

    public ObjectCatalogService(
        ILogger<ObjectCatalogService> logger,
        IDataManager gameData,
        IObjectAssetIndex assetIndex)
    {
        _logger = logger;
        _assetIndex = assetIndex;
        _builder = new ObjectCatalogBuilder(gameData, assetIndex);
        _warmupState = new ObjectWarmupState<ObjectCatalogData>(
            logger,
            BuildCatalog,
            "waiting to load object catalog",
            "building object catalog",
            "object catalog ready",
            "object catalog load failed",
            "failed to build object catalog in background");
        _assetIndex.AssetsChanged += HandleCatalogAssetsChanged;
    }

    public bool IsReady
        => _warmupState.IsReady;

    public bool IsLoading
        => _warmupState.IsLoading;

    public bool HasFailed
        => _warmupState.HasFailed;

    public string StatusText
        => _warmupState.StatusText;

    public void EnsureWarmup()
        => _warmupState.EnsureWarmup();

    public bool TryGetCatalog([NotNullWhen(true)] out ObjectCatalogData? catalog)
        => _warmupState.TryGetValue(out catalog);

    public ObjectCatalogData GetCatalog()
        => _warmupState.GetValue();

    public IReadOnlyList<string> ResolvePreviewModelPaths(ObjectCatalogKind kind, string path)
    {
        if (!TryNormalizeCatalogPath(path, out string normalizedPath))
        {
            return [];
        }

        return GetCatalog().ResolvePreviewModelPaths(kind, normalizedPath);
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

    public bool TryResolveFurnitureMetadata(
        string sharedGroupPath,
        uint housingRowId,
        uint itemRowId,
        [NotNullWhen(true)] out HousingFurnitureMetadata? metadata)
    {
        if (!TryNormalizeCatalogPath(sharedGroupPath, out string normalizedPath))
        {
            metadata = null;
            return false;
        }

        return GetCatalog().TryResolveFurnitureMetadata(normalizedPath, housingRowId, itemRowId, out metadata);
    }

    private static bool TryNormalizeCatalogPath(string path, out string normalizedPath)
        => ObjectPathRules.TryNormalizeGamePath(path, out normalizedPath);

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
        }

        projectionUpdateCancellation?.Cancel();
        _warmupState.Dispose();
        WaitForProjectionUpdate(projectionUpdateTask);
        projectionUpdateCancellation?.Dispose();
    }

    private ObjectCatalogData BuildCatalog(CancellationToken cancellationToken)
    {
        _assetIndex.EnsureWarmup();

        cancellationToken.ThrowIfCancellationRequested();
        long bgObjectSectionVersion = _assetIndex.GetBgObjectSectionVersion(cancellationToken);
        long standaloneVfxSectionVersion = _assetIndex.GetStandaloneVfxSectionVersion(cancellationToken);
        ObjectCatalogData catalog = _builder.Build(cancellationToken);
        _appliedBgObjectSectionVersion = bgObjectSectionVersion;
        _appliedStandaloneVfxSectionVersion = standaloneVfxSectionVersion;

        _logger.LogInformation(
            "built object catalog with {TotalCount} entries, {FurnitureCount} furniture entries, {BgObjectCount} bgobject entries, {VfxCount} vfx entries",
            catalog.EntryCount,
            catalog.Furniture.Count,
            catalog.BgObjects.Count,
            catalog.Vfx.Count);

        return catalog;
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
                    ApplyProjectionUpdate(_warmupState.GetValue(cancellationToken), cancellationToken);
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

        _assetIndex.EnsureWarmup();
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<ObjectCatalogEntry>? bgObjectEntries = null;
        IReadOnlyList<ObjectCatalogEntry>? vfxEntries = null;
        long bgObjectSectionVersion = _assetIndex.GetBgObjectSectionVersion(cancellationToken);
        if (bgObjectSectionVersion != _appliedBgObjectSectionVersion)
        {
            bgObjectEntries = _builder.BuildBgObjectEntries(cancellationToken);
        }

        long standaloneVfxSectionVersion = _assetIndex.GetStandaloneVfxSectionVersion(cancellationToken);
        if (standaloneVfxSectionVersion != _appliedStandaloneVfxSectionVersion)
        {
            vfxEntries = _builder.BuildVfxEntries(cancellationToken);
        }

        if (bgObjectEntries is null && vfxEntries is null)
        {
            return;
        }

        catalog.ReplaceSections(
            bgObjectEntries: bgObjectEntries,
            vfxEntries: vfxEntries);

        if (bgObjectEntries is not null)
        {
            _appliedBgObjectSectionVersion = bgObjectSectionVersion;
        }

        if (vfxEntries is not null)
        {
            _appliedStandaloneVfxSectionVersion = standaloneVfxSectionVersion;
        }
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
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to stop object catalog projection update");
        }
    }
}

