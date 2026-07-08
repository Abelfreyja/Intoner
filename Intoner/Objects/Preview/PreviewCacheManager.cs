using Intoner.Objects.Preview.Assets;

namespace Intoner.Objects.Preview;

internal sealed class PreviewCacheManager(
    PreviewAssetService assetService,
    PreviewCacheStateStore stateStore)
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(1);

    private long _nextCleanupAtMs;

    public void Trim(long now)
    {
        long nextCleanupAtMs = Interlocked.Read(ref _nextCleanupAtMs);
        if (now < nextCleanupAtMs)
        {
            return;
        }

        long next = now + (long)CleanupInterval.TotalMilliseconds;
        if (Interlocked.CompareExchange(ref _nextCleanupAtMs, next, nextCleanupAtMs) != nextCleanupAtMs)
        {
            return;
        }

        stateStore.TrimRenderCaches(now);

        foreach (PreviewAssetState.GeometryEntry evictedEntry in assetService.Trim(now))
        {
            stateStore.DetachGeometryEntry(evictedEntry);
        }
    }
}
