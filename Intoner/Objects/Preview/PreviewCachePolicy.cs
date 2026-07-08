using System.Runtime.InteropServices;

namespace Intoner.Objects.Preview;

internal static class PreviewCachePolicy
{
    private static readonly TimeSpan LoadFailureRetryBaseDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RenderFailureRetryBaseDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxLoadFailureRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxRenderFailureRetryDelay = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan ThumbnailWorkRetention = TimeSpan.FromMilliseconds(900);
    public static readonly TimeSpan ThumbnailTextureRetention = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan ThumbnailAssetRetention = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DetailGeometryRetention = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan DetailAssetRetention = TimeSpan.FromMinutes(5);

    public static readonly PreviewCachePolicy.Mode ThumbnailMode = new(
        48,
        192,
        192,
        ThumbnailWorkRetention,
        ThumbnailTextureRetention,
        ThumbnailAssetRetention,
        96,
        16L * 1024 * 1024);

    public static bool IsThumbnailWorkExpired(long lastAccessAtMs, long now)
        => !HasQueueInterest(lastAccessAtMs, now);

    public static bool HasRecentLoadInterest(PreviewCacheState.Asset state, long now)
        => HasQueueInterest(state.LastThumbnailAccessAtMs, now)
            || HasQueueInterest(state.LastDetailAccessAtMs, now);

    public static bool CanProcessRequestedRender(
        PreviewCacheState.Asset assetState,
        PreviewCacheState.Render renderState,
        long now)
        => assetState.HasLoadedGeometry
            && assetState.GeometryEntry is not null
            && renderState.HasRequestedRequest
            && !renderState.RenderRunning
            && !IsThumbnailWorkExpired(renderState.LastAccessAtMs, now)
            && NeedsRender(renderState, renderState.RequestedRequest, now);

    public static long GetLoadRetryDelayMilliseconds(int failureCount)
        => GetRetryDelayMilliseconds(failureCount, LoadFailureRetryBaseDelay, MaxLoadFailureRetryDelay);

    public static long GetRenderRetryDelayMilliseconds(int failureCount)
        => GetRetryDelayMilliseconds(failureCount, RenderFailureRetryBaseDelay, MaxRenderFailureRetryDelay);

    private static bool HasQueueInterest(long lastAccessAtMs, long now)
        => !IsExpired(lastAccessAtMs, now, ThumbnailWorkRetention);

    private static bool NeedsRender(PreviewCacheState.Render renderState, PreviewRender.Request request, long now)
    {
        if (!renderState.HasRenderedRequest || renderState.LastRenderedRequest != request)
        {
            return true;
        }

        if (renderState.PendingTexture is { } pendingTexture)
        {
            return pendingTexture.Request != request;
        }

        if (renderState.Texture is not null)
        {
            return false;
        }

        return renderState.LastRenderSucceeded
            || now >= renderState.NextRenderRetryAtMs;
    }

    private static long GetRetryDelayMilliseconds(int failureCount, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        int exponent = Math.Clamp(failureCount - 1, 0, 5);
        long scaledDelay = (long)baseDelay.TotalMilliseconds * (1L << exponent);
        return Math.Min(scaledDelay, (long)maxDelay.TotalMilliseconds);
    }

    private static bool IsExpired(long lastAccessAtMs, long now, TimeSpan retention)
    {
        if (lastAccessAtMs <= 0)
        {
            return true;
        }

        return now - lastAccessAtMs >= retention.TotalMilliseconds;
    }

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct Mode(
        int MinDimension,
        int MaxWidth,
        int MaxHeight,
        TimeSpan PendingTextureRetention,
        TimeSpan TextureRetention,
        TimeSpan AssetRetention,
        int MaxTextureCount,
        long MaxTextureBytes);
}
