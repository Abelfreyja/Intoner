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

        string cacheKey = asset.ModelSignature;
        if (_geometryCache.TryGet(cacheKey, out entry))
        {
            error = null;
            return true;
        }

        ModelPreviewGeometryReader.PreviewGeometry? geometry = _geometryBuilder.BuildMergedGeometry(asset.Models, out error);
        if (geometry is null)
        {
            entry = null;
            return false;
        }

        entry = _geometryCache.GetOrCreate(cacheKey, geometry);
        error = null;
        return true;
    }

    public static void MarkGeometryAccess(PreviewAssetState.GeometryEntry entry, PreviewRender.Mode mode, long now)
        => PreviewGeometryCache.MarkAccess(entry, mode, now);

    public static void SyncGeometryAccess(PreviewAssetState.GeometryEntry entry, long lastThumbnailAccessAtMs, long lastDetailAccessAtMs)
        => PreviewGeometryCache.SyncAccess(entry, lastThumbnailAccessAtMs, lastDetailAccessAtMs);

    public void BeginGeometryUse(PreviewAssetState.GeometryEntry entry, PreviewRender.Mode mode, long now)
        => _geometryCache.BeginUse(entry, mode, now);

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
        _textureCache.Clear();
        _materialResolver.Clear();
    }
}
