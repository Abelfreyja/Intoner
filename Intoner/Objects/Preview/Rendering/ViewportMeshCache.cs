using Intoner.Objects.Assets;
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

        lock (geometryEntry.SyncRoot)
        {
            string meshKey = BuildMeshKey(geometryEntry, asset);
            ModelPreviewGeometryReader.PreviewGeometry geometry = geometryEntry.Geometry;

            if (_entries.TryGetValue(meshKey, out Entry? cachedEntry) && cachedEntry.Matches(geometry))
            {
                cachedEntry.LastAccessAtMs = Environment.TickCount64;
                lease = cachedEntry.Mesh.Acquire();
                return true;
            }

            ViewportMesh? mesh = null;

            try
            {
                mesh = ViewportMeshUploader.Upload(
                    device,
                    geometry,
                    asset.UntexturedDiffuseColor,
                    whiteTexture);
                Entry replacement = new(mesh, geometry, Environment.TickCount64);
                lease = mesh.Acquire();
                _entries[meshKey] = replacement;
            }
            catch (Exception ex)
            {
                lease?.Dispose();
                lease = null;
                mesh?.Dispose();
                logger.LogWarning(ex, "preview mesh upload failed");
                error = "Failed to upload preview mesh.";
                return false;
            }

            cachedEntry?.Mesh.Dispose();
            return true;
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

    private sealed class Entry
    {
        private readonly WeakReference<ModelPreviewGeometryReader.PreviewGeometry> _geometry;
        private readonly WeakReference<ModelPreviewGeometryReader.PreviewMaterial>[] _materials;
        private readonly WeakReference<ModelPreviewGeometryReader.PreviewTexture>?[] _textures;

        public Entry(ViewportMesh mesh, ModelPreviewGeometryReader.PreviewGeometry geometry, long lastAccessAtMs)
        {
            Mesh = mesh;
            LastAccessAtMs = lastAccessAtMs;
            _geometry = new(geometry);
            _materials = new WeakReference<ModelPreviewGeometryReader.PreviewMaterial>[geometry.Materials.Length];
            _textures = new WeakReference<ModelPreviewGeometryReader.PreviewTexture>?[geometry.Materials.Length];

            for (int index = 0; index < geometry.Materials.Length; ++index)
            {
                ModelPreviewGeometryReader.PreviewMaterial material = geometry.Materials[index];
                _materials[index] = new(material);
                _textures[index] = material.DiffuseTexture is { } texture ? new(texture) : null;
            }
        }

        public ViewportMesh Mesh { get; }
        public long LastAccessAtMs { get; set; }

        public bool Matches(ModelPreviewGeometryReader.PreviewGeometry geometry)
        {
            if (!_geometry.TryGetTarget(out ModelPreviewGeometryReader.PreviewGeometry? sourceGeometry)
             || !ReferenceEquals(sourceGeometry, geometry)
             || _materials.Length != geometry.Materials.Length)
            {
                return false;
            }

            for (int index = 0; index < _materials.Length; ++index)
            {
                ModelPreviewGeometryReader.PreviewMaterial material = geometry.Materials[index];

                if (!_materials[index].TryGetTarget(out ModelPreviewGeometryReader.PreviewMaterial? sourceMaterial)
                 || !ReferenceEquals(sourceMaterial, material))
                {
                    return false;
                }

                WeakReference<ModelPreviewGeometryReader.PreviewTexture>? source = _textures[index];

                if (source is null)
                {
                    if (material.DiffuseTexture is not null)
                    {
                        return false;
                    }
                }
                else if (!source.TryGetTarget(out ModelPreviewGeometryReader.PreviewTexture? texture)
                      || !ReferenceEquals(texture, material.DiffuseTexture))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
