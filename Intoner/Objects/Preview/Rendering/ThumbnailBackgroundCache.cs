using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Preview.Rendering;

internal static class ThumbnailBackgroundCache
{
    private const int MaxThumbnailBackgroundCacheEntries = 16;

    private static readonly Lock _lock = new();
    private static readonly Dictionary<ThumbnailBackgroundCacheKey, CachedBackgroundState> _cache = new();

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct ThumbnailBackgroundCacheKey(int Width, int Height, PreviewRender.BackgroundStyle BackgroundStyle);

    private sealed class CachedBackgroundState(byte[] pixels)
    {
        public byte[] Pixels { get; } = pixels;
        public long LastAccessAtMs { get; set; } = Environment.TickCount64;
    }

    public static void CopyTo(byte[] pixels, int width, int height, PreviewRender.BackgroundStyle backgroundStyle)
    {
        var cacheKey = new ThumbnailBackgroundCacheKey(width, height, backgroundStyle);
        byte[] backgroundPixels;
        lock (_lock)
        {
            long now = Environment.TickCount64;
            if (!_cache.TryGetValue(cacheKey, out CachedBackgroundState? backgroundState))
            {
                backgroundState = new CachedBackgroundState(Create(width, height, backgroundStyle));
                _cache[cacheKey] = backgroundState;
                Trim();
            }

            backgroundState.LastAccessAtMs = now;
            backgroundPixels = backgroundState.Pixels;
        }

        Buffer.BlockCopy(backgroundPixels, 0, pixels, 0, backgroundPixels.Length);
    }

    private static void Trim()
    {
        if (_cache.Count <= MaxThumbnailBackgroundCacheEntries)
        {
            return;
        }

        foreach (ThumbnailBackgroundCacheKey key in _cache
                     .OrderBy(static entry => entry.Value.LastAccessAtMs)
                     .Take(_cache.Count - MaxThumbnailBackgroundCacheEntries)
                     .Select(static entry => entry.Key)
                     .ToArray())
        {
            _cache.Remove(key);
        }
    }

    private static byte[] Create(int width, int height, PreviewRender.BackgroundStyle backgroundStyle)
    {
        var pixels = new byte[Math.Max(1, width * height * 4)];
        Fill(pixels, width, height, backgroundStyle);
        return pixels;
    }

    private static void Fill(byte[] pixels, int width, int height, PreviewRender.BackgroundStyle backgroundStyle)
    {
        var (top, bottom) = PreviewRender.BackgroundPalette.GetGradient(backgroundStyle);

        for (var y = 0; y < height; y++)
        {
            var t = height <= 1 ? 0f : y / (float)(height - 1);
            var rowColor = Vector3.Lerp(top, bottom, t);
            for (var x = 0; x < width; x++)
            {
                var pixelIndex = ((y * width) + x) * 4;
                ThumbnailPixels.WritePixel(pixels, pixelIndex, rowColor);
            }
        }
    }
}
