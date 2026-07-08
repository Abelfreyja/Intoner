using Intoner.Objects.Preview.Assets;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Preview;

internal sealed class PreviewCacheStateStore(
    PreviewCachePolicy.Mode thumbnailPolicy,
    TimeSpan detailGeometryAssetRetention)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, PreviewCacheState.Asset> _states = new(StringComparer.OrdinalIgnoreCase);

    public PreviewCacheState.Asset GetOrCreate(PreviewAsset asset)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(asset.CacheKey, out PreviewCacheState.Asset? state))
            {
                lock (state.SyncRoot)
                {
                    state.PreviewAsset = asset;
                }

                return state;
            }

            state = new PreviewCacheState.Asset(asset);
            _states.Add(asset.CacheKey, state);
            return state;
        }
    }

    public bool TryGet(string assetKey, out PreviewCacheState.Asset? state)
    {
        lock (_lock)
        {
            return _states.TryGetValue(assetKey, out state);
        }
    }

    public Snapshot GetSnapshot()
    {
        int assetStateCount;
        int loadedAssetStateCount;
        int queuedLoadCount;
        int runningLoadCount;
        int thumbnailTextureCount;
        long thumbnailTextureBytes;
        long pendingThumbnailBytes;

        lock (_lock)
        {
            assetStateCount = _states.Count;
            loadedAssetStateCount = 0;
            queuedLoadCount = 0;
            runningLoadCount = 0;
            thumbnailTextureCount = 0;
            thumbnailTextureBytes = 0;
            pendingThumbnailBytes = 0;

            foreach (PreviewCacheState.Asset state in _states.Values)
            {
                lock (state.SyncRoot)
                {
                    if (state.HasLoadedGeometry)
                    {
                        loadedAssetStateCount++;
                    }

                    if (state.LoadQueued)
                    {
                        queuedLoadCount++;
                    }

                    if (state.LoadRunning)
                    {
                        runningLoadCount++;
                    }

                    PreviewCacheState.Render renderState = state.ThumbnailRenderState;
                    long retainedTextureBytes = renderState.TextureByteCount;
                    long pendingTextureBytes = renderState.PendingTexture?.ByteCount ?? 0;
                    if (retainedTextureBytes > 0)
                    {
                        thumbnailTextureCount++;
                        thumbnailTextureBytes += retainedTextureBytes;
                    }

                    pendingThumbnailBytes += pendingTextureBytes;
                }
            }
        }

        return new Snapshot(
            assetStateCount,
            loadedAssetStateCount,
            queuedLoadCount,
            runningLoadCount,
            thumbnailTextureCount,
            thumbnailTextureBytes,
            pendingThumbnailBytes);
    }

    public void TrimRenderCaches(long now)
    {
        lock (_lock)
        {
            List<RenderTrimCandidate> thumbnailCandidates = [];
            List<string> removableAssetKeys = [];

            foreach ((string assetKey, PreviewCacheState.Asset state) in _states)
            {
                lock (state.SyncRoot)
                {
                    PreviewCacheState.Render renderState = state.ThumbnailRenderState;
                    TrimPendingTexture(renderState, now);

                    if (renderState.HasRetainedTexture())
                    {
                        thumbnailCandidates.Add(new RenderTrimCandidate(state, renderState.LastAccessAtMs));
                    }

                    if (ShouldRemoveState(state, now))
                    {
                        removableAssetKeys.Add(assetKey);
                    }
                }
            }

            TrimModeTextures(
                thumbnailCandidates,
                now,
                thumbnailPolicy);

            foreach (string assetKey in removableAssetKeys)
            {
                if (!_states.TryGetValue(assetKey, out PreviewCacheState.Asset? state))
                {
                    continue;
                }

                lock (state.SyncRoot)
                {
                    if (!ShouldRemoveState(state, now))
                    {
                        continue;
                    }

                    state.ThumbnailRenderState.DisposeResources();
                    state.GeometryEntry = null;
                }

                _states.Remove(assetKey);
            }
        }
    }

    public void DetachGeometryEntry(PreviewAssetState.GeometryEntry entry)
    {
        lock (_lock)
        {
            foreach (PreviewCacheState.Asset state in _states.Values)
            {
                lock (state.SyncRoot)
                {
                    if (!ReferenceEquals(state.GeometryEntry, entry))
                    {
                        continue;
                    }

                    state.GeometryEntry = null;
                    state.HasLoadedGeometry = false;
                    state.NextLoadRetryAtMs = 0;
                    state.Error = null;
                }
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (PreviewCacheState.Asset state in _states.Values)
            {
                lock (state.SyncRoot)
                {
                    state.ThumbnailRenderState.DisposeResources();
                    state.GeometryEntry = null;
                }
            }

            _states.Clear();
        }
    }

    private void TrimPendingTexture(PreviewCacheState.Render renderState, long now)
    {
        if (renderState.PendingTexture is null)
        {
            return;
        }

        if (!IsExpired(renderState.LastAccessAtMs, now, thumbnailPolicy.PendingTextureRetention))
        {
            return;
        }

        renderState.PendingTexture = null;
    }

    private static void TrimModeTextures(
        List<RenderTrimCandidate> renderStates,
        long now,
        PreviewCachePolicy.Mode policy)
    {
        if (renderStates.Count == 0)
        {
            return;
        }

        renderStates.Sort(static (left, right) => left.LastAccessAtMs.CompareTo(right.LastAccessAtMs));
        long totalBytes = 0;
        int remainingCount = 0;
        for (var i = 0; i < renderStates.Count; i++)
        {
            PreviewCacheState.Asset state = renderStates[i].State;
            lock (state.SyncRoot)
            {
                PreviewCacheState.Render renderState = state.ThumbnailRenderState;
                if (!renderState.HasRetainedTexture())
                {
                    continue;
                }

                totalBytes += renderState.GetRetainedTextureByteCount();
                remainingCount++;
            }
        }

        for (var i = 0; i < renderStates.Count; i++)
        {
            PreviewCacheState.Asset state = renderStates[i].State;
            lock (state.SyncRoot)
            {
                PreviewCacheState.Render renderState = state.ThumbnailRenderState;
                if (!renderState.HasRetainedTexture())
                {
                    continue;
                }

                bool shouldEvict = IsExpired(renderState.LastAccessAtMs, now, policy.TextureRetention)
                    || remainingCount > policy.MaxTextureCount
                    || totalBytes > policy.MaxTextureBytes;
                if (!shouldEvict)
                {
                    continue;
                }

                totalBytes -= renderState.GetRetainedTextureByteCount();
                remainingCount--;
                renderState.ReleaseRetainedTextures();
            }
        }
    }

    private bool ShouldRemoveState(PreviewCacheState.Asset state, long now)
    {
        if (state.LoadQueued || state.LoadRunning)
        {
            return false;
        }

        if (state.ThumbnailRenderState.HasActiveResources())
        {
            return false;
        }

        return IsExpired(state.LastThumbnailAccessAtMs, now, thumbnailPolicy.AssetRetention)
            && IsExpired(state.LastDetailAccessAtMs, now, detailGeometryAssetRetention);
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
    internal readonly record struct Snapshot(
        int AssetStateCount,
        int LoadedAssetStateCount,
        int QueuedLoadCount,
        int RunningLoadCount,
        int ThumbnailTextureCount,
        long ThumbnailTextureBytes,
        long PendingThumbnailBytes);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct RenderTrimCandidate(PreviewCacheState.Asset State, long LastAccessAtMs);
}
