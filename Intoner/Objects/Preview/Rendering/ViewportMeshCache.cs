using Intoner.Objects.Preview.Assets;
using Intoner.Services.Gpu;
using Microsoft.Extensions.Logging;
using Device = SharpDX.Direct3D11.Device;

namespace Intoner.Objects.Preview.Rendering;

internal sealed class ViewportMeshCache(ILogger logger) : IDisposable
{
    private static readonly TimeSpan MeshCacheRetention = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MeshCacheTrimInterval = TimeSpan.FromSeconds(1);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private long _nextTrimAtMs;

    public bool TryGetOrUpload(
        Device device,
        PreviewAssetState.GeometryEntry geometryEntry,
        PreviewAsset asset,
        GpuTextureView whiteTexture,
        out GpuLeasedResource<ViewportMesh>.Lease? lease,
        out string? error)
    {
        lease = null;
        error = null;
        string meshKey = BuildMeshKey(geometryEntry, asset);
        if (_entries.TryGetValue(meshKey, out Entry? cachedEntry))
        {
            cachedEntry.LastAccessAtMs = Environment.TickCount64;
            lease = cachedEntry.Mesh.Acquire();
            return true;
        }

        try
        {
            ViewportMesh mesh = ViewportMeshUploader.Upload(
                device,
                geometryEntry.Geometry,
                asset.UntexturedDiffuseColor,
                whiteTexture);
            _entries[meshKey] = new Entry(mesh, Environment.TickCount64);
            lease = mesh.Acquire();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "preview mesh upload failed");
            error = "Failed to upload preview mesh.";
            return false;
        }
    }

    public void Trim(long now)
    {
        if (now < _nextTrimAtMs)
        {
            return;
        }

        _nextTrimAtMs = now + (long)MeshCacheTrimInterval.TotalMilliseconds;
        foreach ((string cacheKey, Entry entry) in _entries.ToArray())
        {
            if (now - entry.LastAccessAtMs < MeshCacheRetention.TotalMilliseconds)
            {
                continue;
            }

            entry.Mesh.Dispose();
            _entries.Remove(cacheKey);
        }
    }

    public void Dispose()
        => Clear();

    public void Clear()
    {
        foreach (Entry entry in _entries.Values)
        {
            entry.Mesh.Dispose();
        }

        _entries.Clear();
    }

    private static string BuildMeshKey(PreviewAssetState.GeometryEntry geometryEntry, PreviewAsset asset)
        => $"{geometryEntry.CacheKey}:{asset.MaterialSignature}";

    private sealed class Entry(ViewportMesh mesh, long lastAccessAtMs)
    {
        public ViewportMesh Mesh { get; } = mesh;
        public long LastAccessAtMs { get; set; } = lastAccessAtMs;
    }
}
