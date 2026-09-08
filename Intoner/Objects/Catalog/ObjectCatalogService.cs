using Intoner.Objects.Assets;
using Intoner.Objects.Utils;
using Intoner.Services.Loading;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

using Intoner.Utils;

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
        => _load.TryGet(out catalog);

    public ObjectCatalogData GetCatalog()
    {
        EnsureLoaded();
        return _load.Get();
    }

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
        => GameAssetPathRules.TryNormalizeGamePath(path, out normalizedPath);

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
        _load.Dispose();
        WaitForProjectionUpdate(projectionUpdateTask);
        projectionUpdateCancellation?.Dispose();
    }

    private ObjectCatalogData BuildCatalog(LoadProgress progress)
    {
        CancellationToken cancellationToken = progress.CancellationToken;
        long startedAt = Stopwatch.GetTimestamp();

        cancellationToken.ThrowIfCancellationRequested();
        long bgObjectSectionVersion = _assetIndex.GetBgObjectSectionVersion(cancellationToken);
        long standaloneVfxSectionVersion = _assetIndex.GetStandaloneVfxSectionVersion(cancellationToken);
        TimeSpan assetWaitTime = Stopwatch.GetElapsedTime(startedAt);
        ObjectCatalogData catalog = _builder.Build(progress);
        TimeSpan totalTime = Stopwatch.GetElapsedTime(startedAt);
        _appliedBgObjectSectionVersion = bgObjectSectionVersion;
        _appliedStandaloneVfxSectionVersion = standaloneVfxSectionVersion;

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
        long bgObjectSectionVersion = _assetIndex.GetBgObjectSectionVersion(cancellationToken);
        if (bgObjectSectionVersion != _appliedBgObjectSectionVersion)
        {
            bgObjectEntries = _builder.BuildBgObjectEntries(LoadProgress.Unreported(cancellationToken));
        }

        long standaloneVfxSectionVersion = _assetIndex.GetStandaloneVfxSectionVersion(cancellationToken);
        if (standaloneVfxSectionVersion != _appliedStandaloneVfxSectionVersion)
        {
            vfxEntries = _builder.BuildVfxEntries(LoadProgress.Unreported(cancellationToken));
        }

        if (bgObjectEntries is null && vfxEntries is null)
        {
            return;
        }

        catalog.ReplaceSections(
            bgObjectEntries: bgObjectEntries,
            vfxEntries: vfxEntries,
            cancellationToken: cancellationToken);

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
            // cancellation is expected during disposal
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to stop object catalog projection update");
        }
    }
}

