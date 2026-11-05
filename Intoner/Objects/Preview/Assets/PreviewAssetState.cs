using Intoner.Objects.Assets;
using System.Collections.Frozen;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Preview.Assets;

internal static class PreviewAssetState
{
    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct DebugSnapshot(
        int SharedGeometryCount,
        long SharedGeometryBytes,
        int SharedGeometryUseCount,
        int DecodedTextureCount,
        long DecodedTextureBytes);

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct MaterialRequest(int Index, string ModelPath, string Name);

    internal sealed class ModelState(int count, IReadOnlySet<int> loadedIndices)
    {
        private volatile string? _error;

        public object SyncRoot { get; } = new();
        public int Count { get; } = count;
        public IReadOnlySet<int> LoadedIndices { get; } = loadedIndices.ToFrozenSet();
        public bool IsIncomplete => LoadedIndices.Count < Count;
        public string? Error => _error;
        public long NextRetryAtMs { get; set; } = loadedIndices.Count < count
            ? Environment.TickCount64 + (long)ObjectAssetFileUtility.AssetReadRetryDelay.TotalMilliseconds
            : 0;

        public void Invalidate(string error)
        {
            lock (SyncRoot)
            {
                _error = error;
            }
        }

        public bool NeedsRetry(long now)
        {
            if (!IsIncomplete)
            {
                return false;
            }

            lock (SyncRoot)
            {
                return now >= NextRetryAtMs;
            }
        }
    }

    internal sealed class GeometryEntry(
        string cacheKey,
        ModelPreviewGeometryReader.PreviewGeometry geometry,
        IReadOnlyList<MaterialRequest>? missingMaterials,
        ModelState models)
    {
        public object SyncRoot => Models.SyncRoot;
        public ModelState Models { get; } = models;
        public string CacheKey { get; } = cacheKey;
        public ModelPreviewGeometryReader.PreviewGeometry Geometry { get; } = geometry;
        public long GeometryByteCount { get; } = geometry.EstimatedByteCount;
        public List<MaterialRequest> MissingMaterials { get; } = missingMaterials is null ? [] : [.. missingMaterials];
        public long NextMaterialRetryAtMs { get; set; } = missingMaterials is { Count: > 0 }
            ? Environment.TickCount64 + (long)ObjectAssetFileUtility.AssetReadRetryDelay.TotalMilliseconds
            : 0;
        public long NextTextureRetryAtMs { get; set; }
        public long LastThumbnailAccessAtMs { get; set; }
        public long LastDetailAccessAtMs { get; set; }
        public int ActiveUseCount { get; set; }
    }

    [StructLayout(LayoutKind.Auto)]
    internal readonly struct GeometryLease : IDisposable
    {
        internal GeometryLease(GeometryEntry entry)
        {
            Entry = entry;
        }

        public GeometryEntry? Entry { get; }

        public bool HasEntry
            => Entry is not null;

        public void Dispose()
        {
            if (Entry is not null)
            {
                PreviewAssetService.EndGeometryUse(Entry);
            }
        }
    }
}
