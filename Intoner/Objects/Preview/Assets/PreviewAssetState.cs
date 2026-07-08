using Intoner.Objects.Assets;
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

    internal sealed class GeometryEntry(string cacheKey, ModelPreviewGeometryReader.PreviewGeometry geometry)
    {
        public object SyncRoot { get; } = new();

        public string CacheKey { get; } = cacheKey;
        public ModelPreviewGeometryReader.PreviewGeometry Geometry { get; } = geometry;
        public long GeometryByteCount { get; } = geometry.EstimatedByteCount;
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
