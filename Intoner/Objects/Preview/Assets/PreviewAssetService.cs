using Dalamud.Plugin.Services;
using Intoner.Objects.Assets;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Preview.Assets;

internal sealed class PreviewAssetService : IDisposable
{
    private readonly PreviewTextureCache     _textureCache;
    private readonly PreviewMaterialResolver _materialResolver;
    private readonly PreviewGeometryBuilder  _geometryBuilder;
    private readonly PreviewGeometryCache    _geometryCache;

    private bool _disposed;

    public PreviewAssetService(
        ILogger<PreviewAssetService> logger,
        IDataManager gameData)
    {
        _textureCache     = new PreviewTextureCache(logger, gameData);
        _materialResolver = new PreviewMaterialResolver(logger, gameData, _textureCache);
        _geometryBuilder  = new PreviewGeometryBuilder(gameData, _materialResolver);
        _geometryCache    = new PreviewGeometryCache(_materialResolver);
    }

    public bool TryGetOrBuildGeometry(
        PreviewAsset asset,
        out PreviewAssetState.GeometryEntry? entry,
        out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (asset.Models.Count == 0)
        {
            entry = null;
            error = "Preview unavailable for this asset.";
            return false;
        }

        error = null;
        long now = Environment.TickCount64;
        string cacheKey = asset.ModelSignature;

        if (!_geometryCache.TryGet(cacheKey, out entry) || entry is null || entry.NeedsModelRetry(now))
        {
            if (entry is not null)
            {
                lock (entry.SyncRoot)
                {
                    entry.NextModelRetryAtMs = now + (long)ObjectAssetFileUtility.AssetReadRetryDelay.TotalMilliseconds;
                }
            }

            try
            {
                ModelPreviewGeometryReader.PreviewGeometry? geometry = _geometryBuilder.BuildMergedGeometry(asset.Models,
                    out IReadOnlyList<PreviewAssetState.MaterialRequest> missingMaterials,
                    out IReadOnlySet<int> loadedModelIndices, out error);

                if (geometry is not null)
                {
                    entry = _geometryCache.GetOrCreate(cacheKey, geometry, missingMaterials, asset.Models.Count, loadedModelIndices);
                }
            }
            catch (Exception ex) when (entry is not null && ObjectAssetFileUtility.IsReadFailure(ex))
            {
                error = null;
            }
        }

        if (entry is null)
        {
            return false;
        }

        if (NeedsAssetRetry(entry, now))
        {
            entry = _geometryCache.RefreshResources(entry, now);
        }

        error = null;
        return true;
    }

    public bool NeedsAssetRetry(PreviewAssetState.GeometryEntry entry, long now)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_geometryCache.TryGet(entry.CacheKey, out PreviewAssetState.GeometryEntry? current) && !ReferenceEquals(current, entry))
        {
            return true;
        }

        lock (entry.SyncRoot)
        {
            long retryAtMs = GetNextAssetRetryAtMs(entry);
            return retryAtMs > 0 && now >= retryAtMs;
        }
    }

    private static long GetNextAssetRetryAtMs(PreviewAssetState.GeometryEntry entry)
    {
        long retryAtMs = entry.HasMissingModels ? entry.NextModelRetryAtMs : 0;

        if (entry.NextMaterialRetryAtMs > 0 && (retryAtMs == 0 || entry.NextMaterialRetryAtMs < retryAtMs))
        {
            retryAtMs = entry.NextMaterialRetryAtMs;
        }

        if (entry.NextTextureRetryAtMs > 0 && (retryAtMs == 0 || entry.NextTextureRetryAtMs < retryAtMs))
        {
            retryAtMs = entry.NextTextureRetryAtMs;
        }

        return retryAtMs;
    }

    public static void MarkGeometryAccess(PreviewAssetState.GeometryEntry entry, PreviewRender.Mode mode, long now)
        => PreviewGeometryCache.MarkAccess(entry, mode, now);

    public static void SyncGeometryAccess(PreviewAssetState.GeometryEntry entry, long lastThumbnailAccessAtMs, long lastDetailAccessAtMs)
        => PreviewGeometryCache.SyncAccess(entry, lastThumbnailAccessAtMs, lastDetailAccessAtMs);

    public void BeginGeometryUse(PreviewAssetState.GeometryEntry entry, PreviewRender.Mode mode, long now)
        => _geometryCache.BeginUse(entry, mode, now);

    public ModelPreviewGeometryReader.PreviewGeometry CaptureRenderGeometry(
        PreviewAssetState.GeometryEntry entry,
        out long nextAssetRetryAtMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (entry.SyncRoot)
        {
            ModelPreviewGeometryReader.PreviewGeometry snapshot = entry.Geometry.CreateRenderSnapshot();
            nextAssetRetryAtMs = GetNextAssetRetryAtMs(entry);

            return snapshot;
        }
    }

    public static void EndGeometryUse(PreviewAssetState.GeometryEntry entry)
        => PreviewGeometryCache.EndUse(entry);

    public PreviewAssetState.DebugSnapshot GetDebugSnapshot()
    {
        PreviewGeometryCache.Snapshot geometrySnapshot = _geometryCache.GetSnapshot();
        PreviewTextureCache.Snapshot textureSnapshot = _textureCache.GetSnapshot();
        return new PreviewAssetState.DebugSnapshot(
            geometrySnapshot.Count,
            geometrySnapshot.ByteCount,
            geometrySnapshot.ActiveUseCount,
            textureSnapshot.Count,
            textureSnapshot.ByteCount);
    }

    public IReadOnlyList<PreviewAssetState.GeometryEntry> Trim(long now)
    {
        List<PreviewAssetState.GeometryEntry> evictedEntries = [];
        _geometryCache.Trim(now, evictedEntries);
        _textureCache.Trim(now);
        return evictedEntries;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _geometryCache.Dispose();
        _textureCache.Dispose();
        _materialResolver.Dispose();
    }
}
