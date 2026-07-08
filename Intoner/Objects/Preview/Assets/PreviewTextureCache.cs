using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Intoner.Objects.Assets;
using Lumina.Data.Files;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Preview.Assets;

internal sealed class PreviewTextureCache(ILogger logger, IDataManager gameData)
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
        public long LastAccessAtMs { get; set; } = lastAccessAtMs;
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct Snapshot(int Count, long ByteCount);

    public ModelPreviewGeometryReader.PreviewTexture? GetOrLoad(string texturePath)
    {
        string normalizedTexturePath = GameAssetPathRules.NormalizeGamePath(texturePath);
        if (string.IsNullOrWhiteSpace(normalizedTexturePath))
        {
            return null;
        }

        long now = GetNowMilliseconds();
        lock (_lock)
        {
            if (_entries.TryGetValue(normalizedTexturePath, out Entry? entry))
            {
                entry.LastAccessAtMs = now;
                return entry.Texture;
            }
        }

        ModelPreviewGeometryReader.PreviewTexture? previewTexture = LoadTexture(normalizedTexturePath);
        lock (_lock)
        {
            _entries[normalizedTexturePath] = new Entry(
                previewTexture,
                previewTexture?.RgbaPixels.LongLength ?? 0,
                now);
        }

        return previewTexture;
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
                bool shouldRemove = IsExpired(entry.LastAccessAtMs, now, PreviewTextureCacheRetention)
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

    public void Clear()
    {
        lock (_lock)
        {
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
            byte[] pixels = texFile.GetRgbaImageData();
            if (pixels.Length < width * height * 4)
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

    private static bool IsExpired(long lastAccessAtMs, long now, TimeSpan retention)
    {
        if (lastAccessAtMs <= 0)
        {
            return true;
        }

        return now - lastAccessAtMs >= retention.TotalMilliseconds;
    }
}
