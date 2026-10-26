using Dalamud.Plugin.Services;
using Intoner.Logging;
using Intoner.Objects.Assets;
using Intoner.Objects.Preview.Assets;
using Intoner.Objects.Preview.Rendering;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
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

        if (!TryValidateAsset(asset, out string? error))
        {
            return new PreviewRender.Result(null, false, error);
        }

        var normalizedRequest = NormalizeRequest(request);
        if (normalizedRequest.Mode == PreviewRender.Mode.Detail)
        {
            return GetPreviewGeometry(asset, out _);
        }

        var now = Environment.TickCount64;
        string assetKey = asset.CacheKey;
        var state = _stateStore.GetOrCreate(asset);
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

        _cacheManager.Trim(now);
        return result;
    }

    public PreviewRender.Result GetPreviewGeometry(
        PreviewAsset? asset,
        out PreviewAssetState.GeometryEntry? geometryEntry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        geometryEntry = null;
        if (!TryValidateAsset(asset, out string? error))
        {
            return new PreviewRender.Result(null, false, error);
        }

        var now = Environment.TickCount64;
        string assetKey = asset.CacheKey;
        var state = _stateStore.GetOrCreate(asset);

        PreviewRender.Result result;
        lock (state.SyncRoot)
        {
            state.LastDetailAccessAtMs = now;
            geometryEntry = state.GeometryEntry;
            QueueLoad(assetKey, state, now);

            result = new PreviewRender.Result(
                null,
                state.LoadQueued || state.LoadRunning,
                state.Error);
        }

        if (geometryEntry is not null)
        {
            PreviewAssetService.MarkGeometryAccess(geometryEntry, PreviewRender.Mode.Detail, now);
        }

        _cacheManager.Trim(now);
        return result;
    }

    private static bool TryValidateAsset([NotNullWhen(true)] PreviewAsset? asset, out string? error)
    {
        if (asset is null || !asset.IsValid)
        {
            error = "Select an asset to preview.";
            return false;
        }

        error = asset.HasModels ? null : "Preview unavailable for this asset.";
        return error is null;
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

        _assetService.BeginGeometryUse(geometryEntry, mode, Environment.TickCount64);
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
            renderState.RenderFailureCount = 0;
            renderState.NextRenderRetryAtMs = 0;
        }

        renderState.RequestedRequest = request;
        renderState.HasRequestedRequest = true;

        QueueLoad(assetKey, assetState, now);

        if (!assetState.HasLoadedGeometry)
        {
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
        if (assetState.LoadQueued || assetState.LoadRunning || assetState.NextLoadRetryAtMs > now
         || (assetState.GeometryEntry is { } entry && !_assetService.NeedsAssetRetry(entry, now)))
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

        _scheduler.EnqueueRender(assetKey);
    }

    private void ProcessLoadWork(string assetKey)
    {
        if (!_stateStore.TryGet(assetKey, out PreviewCacheState.Asset? state) || state is null)
        {
            return;
        }

        lock (state.SyncRoot)
        {
            if (state.LoadRunning || !state.LoadQueued)
            {
                return;
            }

            state.LoadQueued = false;
            if (!PreviewCachePolicy.HasRecentLoadInterest(state, Environment.TickCount64))
            {
                return;
            }

            state.LoadRunning = true;
            state.ThumbnailRenderState.RenderCancellation?.Cancel();
        }

        LoadGeometry(assetKey, state);
    }

    private void ProcessRenderWork(string assetKey)
    {
        if (!_stateStore.TryGet(assetKey, out PreviewCacheState.Asset? state) || state is null)
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
            if (!PreviewCachePolicy.CanProcessRequestedRender(state, renderState, Environment.TickCount64))
            {
                return;
            }

            geometryEntry = state.GeometryEntry;
            if (geometryEntry is null)
            {
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
            DiagnosticLog.Write(_logger, LogLevel.Warning, ex,
                "Failed to load preview geometry for {Source} {Path}",
                state.PreviewAsset.AssetKey.Source,
                state.PreviewAsset.DisplayPath);
            error = "Failed to load preview geometry.";
            geometryEntry = null;
        }

        long now = Environment.TickCount64;
        var shouldQueueThumbnailRender = false;
        PreviewAssetState.GeometryEntry? attachedGeometryEntry = geometryEntry;
        long lastThumbnailAccessAtMs = 0;
        long lastDetailAccessAtMs = 0;
        bool loadSucceeded = geometryEntry is not null;
        lock (state.SyncRoot)
        {
            state.LoadRunning = false;

            if (_disposed)
            {
                return;
            }

            if (!loadSucceeded && state.GeometryEntry is { } retainedGeometry)
            {
                geometryEntry = retainedGeometry;
                attachedGeometryEntry = retainedGeometry;
            }

            PreviewCacheState.Render renderState = state.ThumbnailRenderState;
            renderState.PendingTexture = null;
            renderState.NextAssetRetryAtMs = now;
            renderState.RenderCancellation?.Cancel();

            state.GeometryEntry = geometryEntry;
            state.Error = error;
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

            shouldQueueThumbnailRender = PreviewCachePolicy.CanProcessRequestedRender(state, renderState, now)
                && TryMarkRenderQueued(renderState);
        }

        if (attachedGeometryEntry is not null)
        {
            PreviewAssetService.SyncGeometryAccess(attachedGeometryEntry, lastThumbnailAccessAtMs, lastDetailAccessAtMs);
        }

        if (shouldQueueThumbnailRender)
        {
            _scheduler.EnqueueRender(assetKey);
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
        PreviewAssetState.GeometryLease lease = default;

        try
        {
            long startedAt = Environment.TickCount64;
            _assetService.BeginGeometryUse(geometryEntry, PreviewRender.Mode.Thumbnail, startedAt);
            lease = new PreviewAssetState.GeometryLease(geometryEntry);
            ModelPreviewGeometryReader.PreviewGeometry geometry = _assetService.CaptureRenderGeometry(geometryEntry, out long nextAssetRetryAtMs);
            byte[] pixels = ThumbnailRenderer.Render(
                geometry,
                state.PreviewAsset.UntexturedDiffuseColor,
                request.ToCameraState(),
                request.Width,
                request.Height,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            pendingTexture = new PreviewCacheState.PendingThumbnailTexture(
                request,
                pixels,
                state.PreviewAsset.CreateTextureName("Objects.Preview.Thumbnail"),
                nextAssetRetryAtMs);
        }
        catch (OperationCanceledException)
        {
            wasCanceled = true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(_logger, LogLevel.Warning, ex,
                "Failed to render thumbnail preview for {Source} {Path}",
                state.PreviewAsset.AssetKey.Source,
                state.PreviewAsset.DisplayPath);
            error = "Failed to render preview.";
        }
        finally
        {
            lease.Dispose();
        }

        var now = Environment.TickCount64;
        bool shouldRequeue = false;
        lock (state.SyncRoot)
        {
            PreviewCacheState.Render renderState = state.ThumbnailRenderState;
            renderState.RenderRunning = false;
            renderState.HasActiveRenderRequest = false;
            renderState.RenderCancellation?.Dispose();
            renderState.RenderCancellation = null;

            if (_disposed)
            {
                return;
            }

            if (!wasCanceled && !cancellationToken.IsCancellationRequested && ReferenceEquals(state.GeometryEntry, geometryEntry)
             && renderState.HasRequestedRequest && renderState.RequestedRequest == request)
            {
                if (pendingTexture is not null
                 && !PreviewCachePolicy.IsThumbnailWorkExpired(renderState.LastAccessAtMs, now))
                {
                    renderState.PendingTexture = pendingTexture;
                    renderState.Error = null;
                }
                else
                {
                    PreviewCachePolicy.RecordRenderFailure(renderState, request, now, error ?? "Failed to render preview.");
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
            _scheduler.EnqueueRender(assetKey);
        }
    }
}
