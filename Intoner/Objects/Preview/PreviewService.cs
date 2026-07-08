using Dalamud.Plugin.Services;
using Intoner.Objects.Preview.Assets;
using Intoner.Objects.Preview.Rendering;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Preview;

internal sealed class PreviewService : IDisposable
{
    private const int LoadWorkerCount = 1;
    private const int RenderWorkerCount = 2;

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct PreviewDebugSnapshot(
        int AssetStateCount,
        int LoadedAssetStateCount,
        int QueuedLoadCount,
        int RunningLoadCount,
        int SharedGeometryCount,
        long SharedGeometryBytes,
        int SharedGeometryRenderCount,
        int ThumbnailTextureCount,
        long ThumbnailTextureBytes,
        long PendingThumbnailBytes,
        int DecodedTextureCount,
        long DecodedTextureBytes,
        int LoadQueueDepth,
        int ThumbnailRenderQueueDepth);

    private readonly ILogger<PreviewService>  _logger;
    private readonly PreviewAssetService      _assetService;
    private readonly PreviewCacheStateStore   _stateStore;
    private readonly PreviewCacheManager      _cacheManager;
    private readonly ThumbnailTextureUploader _textureUploader;
    private readonly PreviewWorkScheduler     _scheduler;

    private bool _disposed;

    public PreviewService(
        ILogger<PreviewService> logger,
        PreviewAssetService assetService,
        ITextureProvider textureProvider)
    {
        _logger          = logger;
        _assetService    = assetService;
        _stateStore      = new PreviewCacheStateStore(
            PreviewCachePolicy.ThumbnailMode,
            PreviewCachePolicy.DetailAssetRetention);
        _cacheManager    = new PreviewCacheManager(_assetService, _stateStore);
        _textureUploader = new ThumbnailTextureUploader(_logger, textureProvider);
        _scheduler       = new PreviewWorkScheduler(
            LoadWorkerCount,
            RenderWorkerCount,
            ProcessLoadWork,
            ProcessRenderWork);
    }

    public PreviewRender.Result GetPreview(PreviewAsset? asset, PreviewRender.Request request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (asset is null || !asset.IsValid)
        {
            return new PreviewRender.Result(null, false, "Select an asset to preview.");
        }

        if (!asset.HasModels)
        {
            return new PreviewRender.Result(null, false, "Preview unavailable for this asset.");
        }

        var normalizedRequest = NormalizeRequest(request);
        if (normalizedRequest.Mode == PreviewRender.Mode.Detail)
        {
            return GetPreviewGeometry(asset, out _);
        }

        var now = GetNowMilliseconds();
        string assetKey = asset.CacheKey;
        var state = GetOrCreateState(asset);
        PreviewRender.Result result;
        PreviewAssetState.GeometryEntry? geometryEntry = null;

        lock (state.SyncRoot)
        {
            PreviewCacheState.Render renderState = state.ThumbnailRenderState;
            MarkThumbnailAccess(state, renderState, now);
            geometryEntry = state.GeometryEntry;
            _textureUploader.ApplyPendingTexture(renderState, normalizedRequest, now);
            EnsureThumbnailWork(assetKey, state, renderState, normalizedRequest, now);

            var isLoading = state.LoadQueued
                || state.LoadRunning
                || renderState.RenderQueued
                || renderState.RenderRunning
                || renderState.PendingTexture is not null;
            if (!isLoading
             && PreviewCachePolicy.CanProcessRequestedRender(state, renderState, now))
            {
                isLoading = true;
            }

            result = new PreviewRender.Result(
                renderState.Texture,
                isLoading,
                renderState.Error ?? state.Error);
        }

        if (geometryEntry is not null)
        {
            PreviewAssetService.MarkGeometryAccess(geometryEntry, PreviewRender.Mode.Thumbnail, now);
        }

        MaybeTrimCaches(now);
        return result;
    }

    public PreviewRender.Result GetPreviewGeometry(
        PreviewAsset? asset,
        out PreviewAssetState.GeometryEntry? geometryEntry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        geometryEntry = null;
        if (asset is null || !asset.IsValid)
        {
            return new PreviewRender.Result(null, false, "Select an asset to preview.");
        }

        if (!asset.HasModels)
        {
            return new PreviewRender.Result(null, false, "Preview unavailable for this asset.");
        }

        var now = GetNowMilliseconds();
        string assetKey = asset.CacheKey;
        var state = GetOrCreateState(asset);

        PreviewRender.Result result;
        lock (state.SyncRoot)
        {
            state.LastDetailAccessAtMs = now;
            geometryEntry = state.GeometryEntry;
            if (!state.HasLoadedGeometry)
            {
                QueueLoad(assetKey, state, now);
            }

            result = new PreviewRender.Result(
                null,
                state.LoadQueued || state.LoadRunning,
                state.Error);
        }

        if (geometryEntry is not null)
        {
            PreviewAssetService.MarkGeometryAccess(geometryEntry, PreviewRender.Mode.Detail, now);
        }

        MaybeTrimCaches(now);
        return result;
    }

    public PreviewRender.Result AcquirePreviewGeometry(
        PreviewAsset? asset,
        PreviewRender.Mode mode,
        out PreviewAssetState.GeometryLease lease)
    {
        PreviewRender.Result result = GetPreviewGeometry(asset, out PreviewAssetState.GeometryEntry? geometryEntry);
        if (geometryEntry is null)
        {
            lease = default;
            return result;
        }

        _assetService.BeginGeometryUse(geometryEntry, mode, GetNowMilliseconds());
        lease = new PreviewAssetState.GeometryLease(geometryEntry);
        return new PreviewRender.Result(null, false, null);
    }

    public PreviewDebugSnapshot GetDebugSnapshot()
    {
        PreviewAssetState.DebugSnapshot assetSnapshot = _assetService.GetDebugSnapshot();
        var stateSnapshot = _stateStore.GetSnapshot();

        return new PreviewDebugSnapshot(
            stateSnapshot.AssetStateCount,
            stateSnapshot.LoadedAssetStateCount,
            stateSnapshot.QueuedLoadCount,
            stateSnapshot.RunningLoadCount,
            assetSnapshot.SharedGeometryCount,
            assetSnapshot.SharedGeometryBytes,
            assetSnapshot.SharedGeometryUseCount,
            stateSnapshot.ThumbnailTextureCount,
            stateSnapshot.ThumbnailTextureBytes,
            stateSnapshot.PendingThumbnailBytes,
            assetSnapshot.DecodedTextureCount,
            assetSnapshot.DecodedTextureBytes,
            _scheduler.LoadQueueDepth,
            _scheduler.RenderQueueDepth);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scheduler.Dispose();
        _stateStore.Clear();
    }

    private PreviewCacheState.Asset GetOrCreateState(PreviewAsset asset)
        => _stateStore.GetOrCreate(asset);

    private bool TryGetState(string assetKey, out PreviewCacheState.Asset? state)
        => _stateStore.TryGet(assetKey, out state);

    private static PreviewRender.Request NormalizeRequest(PreviewRender.Request request)
    {
        return new PreviewRender.Request(
            Math.Clamp(request.Width, PreviewCachePolicy.ThumbnailMode.MinDimension, PreviewCachePolicy.ThumbnailMode.MaxWidth),
            Math.Clamp(request.Height, PreviewCachePolicy.ThumbnailMode.MinDimension, PreviewCachePolicy.ThumbnailMode.MaxHeight),
            request.YawHundredths,
            request.PitchHundredths,
            request.ZoomHundredths,
            request.BackgroundStyle,
            request.Mode);
    }

    private static long GetNowMilliseconds()
        => Environment.TickCount64;

    private static void MarkThumbnailAccess(PreviewCacheState.Asset assetState, PreviewCacheState.Render renderState, long now)
    {
        renderState.LastAccessAtMs = now;
        assetState.LastThumbnailAccessAtMs = now;
    }

    private void EnsureThumbnailWork(
        string assetKey,
        PreviewCacheState.Asset assetState,
        PreviewCacheState.Render renderState,
        PreviewRender.Request request,
        long now)
    {
        if (!renderState.HasRequestedRequest || renderState.RequestedRequest != request)
        {
            renderState.Error = null;
        }

        renderState.RequestedRequest = request;
        renderState.HasRequestedRequest = true;

        if (!assetState.HasLoadedGeometry)
        {
            QueueLoad(assetKey, assetState, now);
            return;
        }

        if (renderState.RenderRunning)
        {
            if (!renderState.HasActiveRenderRequest || renderState.ActiveRenderRequest != request)
            {
                renderState.RenderCancellation?.Cancel();
            }

            return;
        }

        if (!PreviewCachePolicy.CanProcessRequestedRender(assetState, renderState, now))
        {
            return;
        }

        QueueRender(assetKey, renderState);
    }

    private void QueueLoad(string assetKey, PreviewCacheState.Asset assetState, long now)
    {
        if (assetState.LoadQueued || assetState.LoadRunning || assetState.HasLoadedGeometry || assetState.NextLoadRetryAtMs > now)
        {
            return;
        }

        assetState.LoadQueued = true;
        _scheduler.EnqueueLoad(assetKey);
    }

    private static bool TryMarkRenderQueued(PreviewCacheState.Render renderState)
    {
        if (renderState.RenderQueued || renderState.RenderRunning || !renderState.HasRequestedRequest)
        {
            return false;
        }

        renderState.RenderQueued = true;
        return true;
    }

    private void QueueRender(string assetKey, PreviewCacheState.Render renderState)
    {
        if (!TryMarkRenderQueued(renderState))
        {
            return;
        }

        QueueRenderWork(assetKey);
    }

    private void QueueRenderWork(string assetKey)
    {
        _scheduler.EnqueueRender(assetKey);
    }

    private void ProcessLoadWork(string assetKey)
    {
        if (!TryGetState(assetKey, out PreviewCacheState.Asset? state) || state is null)
        {
            return;
        }

        lock (state.SyncRoot)
        {
            if (state.HasLoadedGeometry || state.LoadRunning || !state.LoadQueued)
            {
                return;
            }

            state.LoadQueued = false;
            if (!PreviewCachePolicy.HasRecentLoadInterest(state, GetNowMilliseconds()))
            {
                return;
            }

            state.LoadRunning = true;
        }

        LoadGeometry(assetKey, state);
    }

    private void ProcessRenderWork(string assetKey)
    {
        if (!TryGetState(assetKey, out PreviewCacheState.Asset? state) || state is null)
        {
            return;
        }

        PreviewAssetState.GeometryEntry? geometryEntry = null;
        PreviewRender.Request request;
        CancellationTokenSource cancellation;
        lock (state.SyncRoot)
        {
            PreviewCacheState.Render renderState = state.ThumbnailRenderState;
            renderState.RenderQueued = false;
            if (!PreviewCachePolicy.CanProcessRequestedRender(state, renderState, GetNowMilliseconds()))
            {
                return;
            }

            geometryEntry = state.GeometryEntry;
            if (geometryEntry is null)
            {
                state.HasLoadedGeometry = false;
                return;
            }

            cancellation = new CancellationTokenSource();
            renderState.RenderCancellation?.Dispose();
            renderState.RenderCancellation = cancellation;
            renderState.ActiveRenderRequest = renderState.RequestedRequest;
            renderState.HasActiveRenderRequest = true;
            renderState.RenderRunning = true;
            request = renderState.ActiveRenderRequest;
        }

        using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _scheduler.CancellationToken,
            cancellation.Token);
        RenderThumbnailPreview(assetKey, state, geometryEntry, request, linkedCancellation.Token);
    }

    private void LoadGeometry(string assetKey, PreviewCacheState.Asset state)
    {
        string? error = null;
        PreviewAssetState.GeometryEntry? geometryEntry = null;

        try
        {
            _assetService.TryGetOrBuildGeometry(state.PreviewAsset, out geometryEntry, out error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to load preview geometry for {Source} {Path}",
                state.PreviewAsset.AssetKey.Source,
                state.PreviewAsset.DisplayPath);
            error = "Failed to load preview geometry.";
        }

        long now = GetNowMilliseconds();
        var shouldQueueThumbnailRender = false;
        PreviewAssetState.GeometryEntry? attachedGeometryEntry = geometryEntry;
        long lastThumbnailAccessAtMs = 0;
        long lastDetailAccessAtMs = 0;
        bool loadSucceeded = geometryEntry is not null;
        lock (state.SyncRoot)
        {
            state.GeometryEntry = geometryEntry;
            state.Error = error;
            state.HasLoadedGeometry = loadSucceeded;
            state.LoadRunning = false;
            if (loadSucceeded)
            {
                state.LoadFailureCount = 0;
                state.NextLoadRetryAtMs = 0;
            }
            else
            {
                state.LoadFailureCount++;
                state.NextLoadRetryAtMs = now + PreviewCachePolicy.GetLoadRetryDelayMilliseconds(state.LoadFailureCount);
            }

            lastThumbnailAccessAtMs = state.LastThumbnailAccessAtMs;
            lastDetailAccessAtMs = state.LastDetailAccessAtMs;

            if (state.GeometryEntry is not null)
            {
                shouldQueueThumbnailRender = TryMarkPostLoadThumbnailRenderQueued(state, now);
            }
        }

        if (attachedGeometryEntry is not null)
        {
            PreviewAssetService.SyncGeometryAccess(attachedGeometryEntry, lastThumbnailAccessAtMs, lastDetailAccessAtMs);
        }

        if (shouldQueueThumbnailRender)
        {
            QueueRenderWork(assetKey);
        }
    }

    private void RenderThumbnailPreview(
        string assetKey,
        PreviewCacheState.Asset state,
        PreviewAssetState.GeometryEntry geometryEntry,
        PreviewRender.Request request,
        CancellationToken cancellationToken)
    {
        PreviewCacheState.PendingThumbnailTexture? pendingTexture = null;
        string? error = null;
        var wasCanceled = false;

        try
        {
            _assetService.BeginGeometryUse(geometryEntry, PreviewRender.Mode.Thumbnail, GetNowMilliseconds());
            byte[] pixels = ThumbnailRenderer.Render(
                geometryEntry.Geometry,
                state.PreviewAsset.UntexturedDiffuseColor,
                request.ToCameraState(),
                request.Width,
                request.Height,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            pendingTexture = new PreviewCacheState.PendingThumbnailTexture(
                request,
                pixels,
                request.Width,
                request.Height,
                state.PreviewAsset.CreateTextureName("Objects.Preview.Thumbnail"));
        }
        catch (OperationCanceledException)
        {
            wasCanceled = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to render thumbnail preview for {Source} {Path}",
                state.PreviewAsset.AssetKey.Source,
                state.PreviewAsset.DisplayPath);
            error = "Failed to render preview.";
        }
        finally
        {
            PreviewAssetService.EndGeometryUse(geometryEntry);
        }

        var now = GetNowMilliseconds();
        bool shouldRequeue = false;
        lock (state.SyncRoot)
        {
            PreviewCacheState.Render renderState = state.ThumbnailRenderState;
            renderState.RenderRunning = false;
            renderState.HasActiveRenderRequest = false;
            renderState.RenderCancellation?.Dispose();
            renderState.RenderCancellation = null;

            if (!wasCanceled && renderState.HasRequestedRequest && renderState.RequestedRequest == request)
            {
                renderState.LastRenderedRequest = request;
                renderState.HasRenderedRequest = true;
                if (pendingTexture is not null
                 && !PreviewCachePolicy.IsThumbnailWorkExpired(renderState.LastAccessAtMs, now))
                {
                    renderState.PendingTexture = pendingTexture;
                    renderState.LastRenderSucceeded = true;
                    renderState.RenderFailureCount = 0;
                    renderState.NextRenderRetryAtMs = 0;
                    renderState.Error = null;
                }
                else if (renderState.Texture is null)
                {
                    renderState.LastRenderSucceeded = false;
                    renderState.RenderFailureCount++;
                    renderState.NextRenderRetryAtMs = now + PreviewCachePolicy.GetRenderRetryDelayMilliseconds(renderState.RenderFailureCount);
                    renderState.Error = error ?? "Failed to render preview.";
                }
            }

            if (PreviewCachePolicy.CanProcessRequestedRender(state, renderState, now)
             && TryMarkRenderQueued(renderState))
            {
                shouldRequeue = true;
            }
        }

        if (shouldRequeue)
        {
            QueueRenderWork(assetKey);
        }
    }

    private static bool TryMarkPostLoadThumbnailRenderQueued(PreviewCacheState.Asset state, long now)
    {
        PreviewCacheState.Render renderState = state.ThumbnailRenderState;
        return PreviewCachePolicy.CanProcessRequestedRender(state, renderState, now)
            && TryMarkRenderQueued(renderState);
    }

    private void MaybeTrimCaches(long now)
        => _cacheManager.Trim(now);
}
