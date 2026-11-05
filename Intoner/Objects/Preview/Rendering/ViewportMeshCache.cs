using Intoner.Objects.Assets;
using Intoner.Objects.Preview.Assets;
using Intoner.Services.Gpu;
using Microsoft.Extensions.Logging;
using Device = SharpDX.Direct3D11.Device;

namespace Intoner.Objects.Preview.Rendering;

internal sealed class ViewportMeshCache(ILogger logger) : IDisposable
{
    private const int MaxMeshCount = 24;
    private const long MaxMeshBytes = 192L * 1024 * 1024;
    private static readonly TimeSpan MeshCacheRetention = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MeshCacheTrimInterval = TimeSpan.FromSeconds(1);

    private readonly Dictionary<(string Geometry, string Material), Entry> _entries = [];

    private long _nextTrimAtMs;
    private long _byteCount;

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
            var meshKey = (geometryEntry.CacheKey, asset.MaterialSignature);
            ModelPreviewGeometryReader.PreviewGeometry geometry = geometryEntry.Geometry;

            if (_entries.TryGetValue(meshKey, out Entry? cachedEntry) && cachedEntry.Matches(geometry))
            {
                cachedEntry.LastAccessAtMs = Environment.TickCount64;
                lease = cachedEntry.Mesh.Acquire();
                Trim();
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
                _byteCount += mesh.EstimatedByteCount - (cachedEntry?.Mesh.EstimatedByteCount ?? 0);
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
            Trim();
            return true;
        }
    }

    private void Trim()
    {
        long now = Environment.TickCount64;

        if (now < _nextTrimAtMs)
        {
            return;
        }

        _nextTrimAtMs = now + (long)MeshCacheTrimInterval.TotalMilliseconds;
        foreach (var pair in _entries.OrderBy(static pair => pair.Value.LastAccessAtMs).ToArray())
        {
            Entry entry = pair.Value;

            if (now - entry.LastAccessAtMs < MeshCacheTrimInterval.TotalMilliseconds
                || now - entry.LastAccessAtMs < MeshCacheRetention.TotalMilliseconds
                    && _entries.Count <= MaxMeshCount && _byteCount <= MaxMeshBytes)
            {
                continue;
            }

            entry.Mesh.Dispose();
            _entries.Remove(pair.Key);
            _byteCount -= entry.Mesh.EstimatedByteCount;
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
        _byteCount = 0;
    }

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
