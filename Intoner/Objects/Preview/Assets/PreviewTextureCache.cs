using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Intoner.Objects.Assets;
using Intoner.Objects.Resources;
using Lumina.Data.Files;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Preview.Assets;

internal sealed class PreviewTextureCache(ILogger logger, IDataManager gameData) : IDisposable
{
    private const int MaxPreviewTextureDimension = 512;
    private const long MaxPreviewTextureCacheBytes = 32L * 1024 * 1024;

    private static readonly TimeSpan PreviewTextureCacheRetention = TimeSpan.FromSeconds(45);

    private sealed class Entry(
        ModelPreviewGeometryReader.PreviewTexture? texture,
        long byteCount,
        long lastAccessAtMs)
    {
        public ModelPreviewGeometryReader.PreviewTexture? Texture { get; } = texture;
        public long ByteCount { get; } = byteCount;
        public long LastLoadAttemptAtMs { get; } = lastAccessAtMs;
        public long LastAccessAtMs { get; set; } = lastAccessAtMs;
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct Snapshot(int Count, long ByteCount);

    public ModelPreviewGeometryReader.PreviewTexture? GetOrLoad(string texturePath)
        => GetOrLoad(texturePath, GetNowMilliseconds());

    internal ModelPreviewGeometryReader.PreviewTexture? GetOrLoad(string texturePath, long now)
    {
        string normalizedTexturePath = ObjectResolvedPath.FromGamePath(texturePath).Path;
        if (string.IsNullOrWhiteSpace(normalizedTexturePath))
        {
            return null;
        }

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(normalizedTexturePath, out Entry? entry))
            {
                entry.LastAccessAtMs = now;

                if (entry.Texture is not null
                 || !PreviewCachePolicy.IsExpired(entry.LastLoadAttemptAtMs, now, ObjectAssetFileUtility.AssetReadRetryDelay))
                {
                    return entry.Texture;
                }

                _entries.Remove(normalizedTexturePath);
            }
        }

        ModelPreviewGeometryReader.PreviewTexture? previewTexture = LoadTexture(normalizedTexturePath);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(normalizedTexturePath, out Entry? existing) && existing.Texture is not null)
            {
                existing.LastAccessAtMs = now;
                return existing.Texture;
            }

            _entries[normalizedTexturePath] = new Entry(
                previewTexture,
                previewTexture?.RgbaPixels.LongLength ?? 0,
                now);
        }

        return previewTexture;
    }

    public long GetNextRetryAtMs(string texturePath, long now)
    {
        string normalizedPath = ObjectResolvedPath.FromGamePath(texturePath).Path;

        if (normalizedPath.Length == 0)
        {
            return 0;
        }

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _entries.TryGetValue(normalizedPath, out Entry? entry) && entry.Texture is null
                ? entry.LastLoadAttemptAtMs + (long)ObjectAssetFileUtility.AssetReadRetryDelay.TotalMilliseconds
                : now;
        }
    }

    public Snapshot GetSnapshot()
    {
        int count;
        long byteCount = 0;
        lock (_lock)
        {
            count = _entries.Count;
            foreach (Entry entry in _entries.Values)
            {
                byteCount += entry.ByteCount;
            }
        }

        return new Snapshot(count, byteCount);
    }

    public void Trim(long now)
    {
        lock (_lock)
        {
            if (_entries.Count == 0)
            {
                return;
            }

            List<KeyValuePair<string, Entry>> candidates = new(_entries);
            candidates.Sort(static (left, right) => left.Value.LastAccessAtMs.CompareTo(right.Value.LastAccessAtMs));

            long totalBytes = 0;
            foreach ((_, Entry entry) in candidates)
            {
                totalBytes += entry.ByteCount;
            }

            foreach ((string key, Entry entry) in candidates)
            {
                long retainedAtMs = entry.Texture is null ? entry.LastLoadAttemptAtMs : entry.LastAccessAtMs;
                TimeSpan retention = entry.Texture is null ? ObjectAssetFileUtility.AssetReadRetryDelay : PreviewTextureCacheRetention;
                bool shouldRemove = PreviewCachePolicy.IsExpired(retainedAtMs, now, retention)
                    || totalBytes > MaxPreviewTextureCacheBytes;
                if (!shouldRemove)
                {
                    continue;
                }

                totalBytes -= entry.ByteCount;
                _entries.Remove(key);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _entries.Clear();
        }
    }

    private ModelPreviewGeometryReader.PreviewTexture? LoadTexture(string normalizedTexturePath)
    {
        try
        {
            TexFile? texFile = gameData.GetFile<TexFile>(normalizedTexturePath);
            if (texFile is null)
            {
                return null;
            }

            int width = texFile.Header.Width;
            int height = texFile.Header.Height;
            long requiredBytes = (long)width * height * 4;
            if (width <= 0 || height <= 0 || requiredBytes > Array.MaxLength)
            {
                return null;
            }

            byte[] pixels = texFile.GetRgbaImageData();
            if (pixels.LongLength < requiredBytes)
            {
                return null;
            }

            if (Math.Max(width, height) > MaxPreviewTextureDimension)
            {
                float scale = MaxPreviewTextureDimension / (float)Math.Max(width, height);
                int targetWidth = Math.Max(1, (int)MathF.Round(width * scale));
                int targetHeight = Math.Max(1, (int)MathF.Round(height * scale));
                pixels = DownscaleRgba(pixels, width, height, targetWidth, targetHeight);
                width = targetWidth;
                height = targetHeight;
            }

            return new ModelPreviewGeometryReader.PreviewTexture(pixels, width, height);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to load preview texture {TexturePath}", normalizedTexturePath);
            return null;
        }
    }

    private static byte[] DownscaleRgba(byte[] sourcePixels, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (targetWidth == sourceWidth && targetHeight == sourceHeight)
        {
            return sourcePixels;
        }

        byte[] scaledPixels = new byte[targetWidth * targetHeight * 4];
        for (int y = 0; y < targetHeight; y++)
        {
            int sourceY = Math.Min(sourceHeight - 1, (int)MathF.Floor(y * sourceHeight / (float)targetHeight));
            for (int x = 0; x < targetWidth; x++)
            {
                int sourceX = Math.Min(sourceWidth - 1, (int)MathF.Floor(x * sourceWidth / (float)targetWidth));
                int sourceIndex = ((sourceY * sourceWidth) + sourceX) * 4;
                int targetIndex = ((y * targetWidth) + x) * 4;
                scaledPixels[targetIndex] = sourcePixels[sourceIndex];
                scaledPixels[targetIndex + 1] = sourcePixels[sourceIndex + 1];
                scaledPixels[targetIndex + 2] = sourcePixels[sourceIndex + 2];
                scaledPixels[targetIndex + 3] = sourcePixels[sourceIndex + 3];
            }
        }

        return scaledPixels;
    }

    private static long GetNowMilliseconds()
        => Environment.TickCount64;
}
