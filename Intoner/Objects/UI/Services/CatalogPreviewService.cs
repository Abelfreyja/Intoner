using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Intoner.Objects.Assets;
using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using Lumina.Data.Files;
using Microsoft.Extensions.Logging;
using PenumbraMtrlFile = Penumbra.GameData.Files.MtrlFile;
using Penumbra.GameData.Files.MaterialStructs;
using ShaderNames = Penumbra.GameData.Files.ShaderStructs.Names;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using MdlDecimator = Intoner.Objects.Assets.ModelPreviewGeometryReader;

namespace Intoner.Objects.UI.Services;

internal sealed class CatalogPreviewService : IDisposable
{
    private const int MaxPreviewTextureDimension = 512;

    private const int LoadWorkerCount = 1;
    private const int RenderWorkerCount = 2;

    private const int MaxThumbnailGeometryCount = 24;
    private const long MaxThumbnailGeometryBytes = 192L * 1024 * 1024;

    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PreviewTextureCacheRetention = TimeSpan.FromSeconds(45);

    private static readonly TimeSpan LoadFailureRetryBaseDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RenderFailureRetryBaseDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxLoadFailureRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxRenderFailureRetryDelay = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan TextureUploadBudgetWindow = TimeSpan.FromMilliseconds(16);

    private const long MaxPreviewTextureCacheBytes = 32L * 1024 * 1024;
    private const int MaxTextureUploadsPerBudgetWindow = 2;

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct PreviewModePolicy(
        int MinDimension,
        int MaxWidth,
        int MaxHeight,
        TimeSpan PendingTextureRetention,
        TimeSpan TextureRetention,
        TimeSpan AssetRetention,
        int MaxTextureCount,
        long MaxTextureBytes);

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
        int DetailTextureCount,
        long DetailTextureBytes,
        long PendingThumbnailBytes,
        long PendingDetailBytes,
        int DecodedTextureCount,
        long DecodedTextureBytes,
        int LoadQueueDepth,
        int ThumbnailRenderQueueDepth,
        int DetailRenderQueueDepth);

    private static readonly PreviewModePolicy ThumbnailPolicy = new(
        48,
        192,
        192,
        TimeSpan.FromMilliseconds(900),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15),
        96,
        16L * 1024 * 1024);

    private static readonly PreviewModePolicy DetailPolicy = new(
        96,
        640,
        480,
        TimeSpan.FromMinutes(3),
        TimeSpan.FromMinutes(3),
        TimeSpan.FromMinutes(5),
        12,
        48L * 1024 * 1024);

    private sealed class PreviewAssetState(ObjectCatalogKind kind, string path)
    {
        public object SyncRoot { get; } = new();

        public ObjectCatalogKind Kind { get; } = kind;
        public string Path { get; } = path;
        public bool LoadQueued { get; set; }
        public bool LoadRunning { get; set; }
        public bool HasLoadedGeometry { get; set; }
        public SharedGeometryCacheEntry? GeometryEntry { get; set; }
        public string? Error { get; set; }
        public int LoadFailureCount { get; set; }
        public long NextLoadRetryAtMs { get; set; }
        public long LastDetailAccessAtMs { get; set; }
        public long LastThumbnailAccessAtMs { get; set; }
        public Dictionary<ObjectCatalogPreviewMode, PreviewRenderState> RenderStates { get; } = [];
    }

    private sealed class PreviewRenderState(ObjectCatalogPreviewMode mode)
    {
        public ObjectCatalogPreviewMode Mode { get; } = mode;
        public IDalamudTextureWrap? Texture { get; set; }
        public long TextureByteCount { get; set; }
        public PendingPreviewTexture? PendingTexture { get; set; }
        public ObjectCatalogPreviewRequest LastRenderedRequest { get; set; }
        public bool HasRenderedRequest { get; set; }
        public bool LastRenderSucceeded { get; set; }
        public ObjectCatalogPreviewRequest RequestedRequest { get; set; }
        public bool HasRequestedRequest { get; set; }
        public ObjectCatalogPreviewRequest ActiveRenderRequest { get; set; }
        public bool HasActiveRenderRequest { get; set; }
        public CancellationTokenSource? RenderCancellation { get; set; }
        public bool RenderQueued { get; set; }
        public bool RenderRunning { get; set; }
        public long LastAccessAtMs { get; set; }
        public string? Error { get; set; }
        public int RenderFailureCount { get; set; }
        public long NextRenderRetryAtMs { get; set; }
    }

    private sealed class PendingPreviewTexture(
        ObjectCatalogPreviewRequest request,
        byte[] pixels,
        int width,
        int height,
        string textureName)
    {
        public ObjectCatalogPreviewRequest Request { get; } = request;
        public byte[] Pixels { get; } = pixels;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public string TextureName { get; } = textureName;
        public long ByteCount { get; } = pixels.LongLength;
    }

    private sealed class CachedPreviewTextureState(MdlDecimator.PreviewTexture? texture, long byteCount, long lastAccessAtMs)
    {
        public MdlDecimator.PreviewTexture? Texture { get; } = texture;
        public long ByteCount { get; } = byteCount;
        public long LastAccessAtMs { get; set; } = lastAccessAtMs;
    }

    private sealed class SharedGeometryCacheEntry(string cacheKey, MdlDecimator.PreviewGeometry geometry)
    {
        public object SyncRoot { get; } = new();

        public string CacheKey { get; } = cacheKey;
        public MdlDecimator.PreviewGeometry Geometry { get; } = geometry;
        public long GeometryByteCount { get; } = geometry.EstimatedByteCount;
        public long LastThumbnailAccessAtMs { get; set; }
        public long LastDetailAccessAtMs { get; set; }
        public int ActiveRenderCount { get; set; }
    }

    private readonly record struct RenderWorkItem(string AssetKey, ObjectCatalogPreviewMode Mode);

    private readonly ILogger<CatalogPreviewService>       _logger;
    private readonly IDataManager                         _gameData;
    private readonly IObjectAssetGameData                 _objectAssetGameData;
    private readonly IObjectCatalogService                _objectCatalog;
    private readonly ITextureProvider                     _textureProvider;

    private readonly Lock _lock = new();
    private readonly Lock _textureUploadBudgetLock = new();
    private readonly Lock _previewTextureCacheLock = new();
    private readonly Lock _materialPathCacheLock = new();

    private readonly Dictionary<string, PreviewAssetState>         _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SharedGeometryCacheEntry>  _sharedGeometryCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedPreviewTextureState> _previewTextureCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?>                   _materialPathCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentQueue<string>         _loadQueue = new();
    private readonly ConcurrentQueue<RenderWorkItem> _detailRenderQueue = new();
    private readonly ConcurrentQueue<RenderWorkItem> _thumbnailRenderQueue = new();
    private readonly SemaphoreSlim                   _loadSignal = new(0);
    private readonly SemaphoreSlim                   _renderSignal = new(0);
    private readonly CancellationTokenSource         _disposeCancellation = new();
    private readonly Task[]                          _loadWorkers;
    private readonly Task[]                          _renderWorkers;

    private long _nextCleanupAtMs;
    private long _nextTextureUploadBudgetResetAtMs;
    private int  _remainingTextureUploadsInWindow;
    private int  _renderDequeueCounter;
    private bool _disposed;

    public CatalogPreviewService(
        ILogger<CatalogPreviewService> logger,
        IDataManager gameData,
        IObjectAssetGameData objectAssetGameData,
        IObjectCatalogService objectCatalog,
        ITextureProvider textureProvider)
    {
        _logger              = logger;
        _gameData            = gameData;
        _objectAssetGameData = objectAssetGameData;
        _objectCatalog       = objectCatalog;
        _textureProvider     = textureProvider;

        _loadWorkers = Enumerable.Range(0, LoadWorkerCount)
            .Select(_ => Task.Factory.StartNew(
                RunLoadWorker,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();
        _renderWorkers = Enumerable.Range(0, RenderWorkerCount)
            .Select(_ => Task.Factory.StartNew(
                RunRenderWorker,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();
    }

    public ObjectCatalogPreviewResult GetPreview(ObjectCatalogKind kind, string path, ObjectCatalogPreviewRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var normalizedPath = ObjectPathRules.NormalizeGamePath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return new ObjectCatalogPreviewResult(null, false, "Select an object to preview.");
        }

        var normalizedRequest = NormalizeRequest(request);
        var now = GetNowMilliseconds();
        var assetKey = BuildAssetKey(kind, normalizedPath);
        var state = GetOrCreateAssetState(assetKey, kind, normalizedPath);
        ObjectCatalogPreviewResult result;
        SharedGeometryCacheEntry? geometryEntry = null;

        lock (state.SyncRoot)
        {
            PreviewRenderState renderState = GetOrCreateRenderState(state, normalizedRequest.Mode);
            MarkPreviewAccess(state, renderState, now);
            geometryEntry = state.GeometryEntry;
            ApplyPendingTexture(renderState, normalizedRequest, now);
            EnsurePreviewWork(assetKey, state, renderState, normalizedRequest, now);

            var isLoading = state.LoadQueued
                || state.LoadRunning
                || renderState.RenderQueued
                || renderState.RenderRunning
                || renderState.PendingTexture is not null;
            if (!isLoading
             && CanProcessRequestedRender(state, renderState, now))
            {
                isLoading = true;
            }

            result = new ObjectCatalogPreviewResult(
                renderState.Texture,
                isLoading,
                renderState.Error ?? state.Error);
        }

        if (geometryEntry is not null)
        {
            MarkGeometryAccess(geometryEntry, normalizedRequest.Mode, now);
        }

        MaybeTrimCaches(now);
        return result;
    }

    public PreviewDebugSnapshot GetDebugSnapshot()
    {
        int assetStateCount;
        int loadedAssetStateCount;
        int queuedLoadCount;
        int runningLoadCount;
        int sharedGeometryCount;
        long sharedGeometryBytes;
        int sharedGeometryRenderCount;
        int thumbnailTextureCount;
        long thumbnailTextureBytes;
        int detailTextureCount;
        long detailTextureBytes;
        long pendingThumbnailBytes;
        long pendingDetailBytes;

        lock (_lock)
        {
            assetStateCount = _states.Count;
            sharedGeometryCount = _sharedGeometryCache.Count;
            sharedGeometryBytes = 0;
            sharedGeometryRenderCount = 0;
            foreach (SharedGeometryCacheEntry entry in _sharedGeometryCache.Values)
            {
                lock (entry.SyncRoot)
                {
                    sharedGeometryBytes += entry.GeometryByteCount;
                    if (entry.ActiveRenderCount > 0)
                    {
                        sharedGeometryRenderCount++;
                    }
                }
            }

            loadedAssetStateCount = 0;
            queuedLoadCount = 0;
            runningLoadCount = 0;
            thumbnailTextureCount = 0;
            thumbnailTextureBytes = 0;
            detailTextureCount = 0;
            detailTextureBytes = 0;
            pendingThumbnailBytes = 0;
            pendingDetailBytes = 0;

            foreach (PreviewAssetState state in _states.Values)
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

                    foreach (PreviewRenderState renderState in state.RenderStates.Values)
                    {
                        long retainedTextureBytes = renderState.TextureByteCount;
                        long pendingTextureBytes = renderState.PendingTexture?.ByteCount ?? 0;
                        switch (renderState.Mode)
                        {
                            case ObjectCatalogPreviewMode.Thumbnail:
                                if (retainedTextureBytes > 0)
                                {
                                    thumbnailTextureCount++;
                                    thumbnailTextureBytes += retainedTextureBytes;
                                }

                                pendingThumbnailBytes += pendingTextureBytes;
                                break;
                            default:
                                if (retainedTextureBytes > 0)
                                {
                                    detailTextureCount++;
                                    detailTextureBytes += retainedTextureBytes;
                                }

                                pendingDetailBytes += pendingTextureBytes;
                                break;
                        }
                    }
                }
            }
        }

        int decodedTextureCount;
        long decodedTextureBytes = 0;
        lock (_previewTextureCacheLock)
        {
            decodedTextureCount = _previewTextureCache.Count;
            foreach (CachedPreviewTextureState cachedTexture in _previewTextureCache.Values)
            {
                decodedTextureBytes += cachedTexture.ByteCount;
            }
        }

        return new PreviewDebugSnapshot(
            assetStateCount,
            loadedAssetStateCount,
            queuedLoadCount,
            runningLoadCount,
            sharedGeometryCount,
            sharedGeometryBytes,
            sharedGeometryRenderCount,
            thumbnailTextureCount,
            thumbnailTextureBytes,
            detailTextureCount,
            detailTextureBytes,
            pendingThumbnailBytes,
            pendingDetailBytes,
            decodedTextureCount,
            decodedTextureBytes,
            _loadQueue.Count,
            _thumbnailRenderQueue.Count,
            _detailRenderQueue.Count);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCancellation.Cancel();

        for (var i = 0; i < LoadWorkerCount; i++)
        {
            _loadSignal.Release();
        }

        for (var i = 0; i < RenderWorkerCount; i++)
        {
            _renderSignal.Release();
        }

        try
        {
            Task.WaitAll(_loadWorkers.Concat(_renderWorkers).ToArray(), 2000);
        }
        catch (AggregateException)
        {
            // ignored during shutdown
        }
        catch (OperationCanceledException)
        {
            // ignored during shutdown
        }

        lock (_lock)
        {
            foreach (PreviewAssetState state in _states.Values)
            {
                lock (state.SyncRoot)
                {
                    foreach (PreviewRenderState renderState in state.RenderStates.Values)
                    {
                        DisposeRenderState(renderState);
                    }

                    state.RenderStates.Clear();
                    state.GeometryEntry = null;
                }
            }

            _states.Clear();
            foreach (SharedGeometryCacheEntry entry in _sharedGeometryCache.Values)
            {
                lock (entry.SyncRoot)
                {
                    ReleaseGeometryMaterialTextures(entry.Geometry);
                    entry.ActiveRenderCount = 0;
                }
            }

            _sharedGeometryCache.Clear();
        }

        _loadSignal.Dispose();
        _renderSignal.Dispose();
        _disposeCancellation.Dispose();
    }

    private PreviewAssetState GetOrCreateAssetState(string assetKey, ObjectCatalogKind kind, string path)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(assetKey, out PreviewAssetState? state))
            {
                return state;
            }

            state = new PreviewAssetState(kind, path);
            _states.Add(assetKey, state);
            return state;
        }
    }

    private bool TryGetAssetState(string assetKey, out PreviewAssetState? state)
    {
        lock (_lock)
        {
            return _states.TryGetValue(assetKey, out state);
        }
    }

    private static PreviewRenderState GetOrCreateRenderState(PreviewAssetState assetState, ObjectCatalogPreviewMode mode)
    {
        if (assetState.RenderStates.TryGetValue(mode, out PreviewRenderState? renderState))
        {
            return renderState;
        }

        renderState = new PreviewRenderState(mode);
        assetState.RenderStates.Add(mode, renderState);
        return renderState;
    }

    private static PreviewModePolicy GetPolicy(ObjectCatalogPreviewMode mode)
        => mode == ObjectCatalogPreviewMode.Thumbnail ? ThumbnailPolicy : DetailPolicy;

    private static ObjectCatalogPreviewRequest NormalizeRequest(ObjectCatalogPreviewRequest request)
    {
        PreviewModePolicy policy = GetPolicy(request.Mode);

        return new ObjectCatalogPreviewRequest(
            Math.Clamp(request.Width, policy.MinDimension, policy.MaxWidth),
            Math.Clamp(request.Height, policy.MinDimension, policy.MaxHeight),
            request.YawHundredths,
            request.PitchHundredths,
            request.ZoomHundredths,
            request.BackgroundStyle,
            request.Mode);
    }

    private static string BuildAssetKey(ObjectCatalogKind kind, string path)
        => $"{kind}:{path}";

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

    private static bool NeedsRender(PreviewRenderState renderState, ObjectCatalogPreviewRequest request, long now)
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

    private static bool HasActiveRenderResources(PreviewRenderState renderState)
        => renderState.Texture is not null
            || renderState.PendingTexture is not null
            || renderState.RenderQueued
            || renderState.RenderRunning;

    private static bool HasQueueInterest(long lastAccessAtMs, long now)
        => !IsExpired(lastAccessAtMs, now, ThumbnailPolicy.PendingTextureRetention);

    private static bool IsThumbnailWorkExpired(ObjectCatalogPreviewMode mode, long lastAccessAtMs, long now)
        => mode == ObjectCatalogPreviewMode.Thumbnail
            && !HasQueueInterest(lastAccessAtMs, now);

    private static bool HasRecentLoadInterest(PreviewAssetState state, long now)
        => HasQueueInterest(state.LastThumbnailAccessAtMs, now)
            || HasQueueInterest(state.LastDetailAccessAtMs, now);

    private static bool CanProcessRequestedRender(
        PreviewAssetState assetState,
        PreviewRenderState renderState,
        long now)
        => assetState.HasLoadedGeometry
            && assetState.GeometryEntry is not null
            && renderState.HasRequestedRequest
            && !renderState.RenderRunning
            && !IsThumbnailWorkExpired(renderState.Mode, renderState.LastAccessAtMs, now)
            && NeedsRender(renderState, renderState.RequestedRequest, now);

    private static long GetRetryDelayMilliseconds(int failureCount, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        int exponent = Math.Clamp(failureCount - 1, 0, 5);
        long scaledDelay = (long)baseDelay.TotalMilliseconds * (1L << exponent);
        return Math.Min(scaledDelay, (long)maxDelay.TotalMilliseconds);
    }

    private static long GetLoadRetryDelayMilliseconds(int failureCount)
        => GetRetryDelayMilliseconds(failureCount, LoadFailureRetryBaseDelay, MaxLoadFailureRetryDelay);

    private static long GetRenderRetryDelayMilliseconds(int failureCount)
        => GetRetryDelayMilliseconds(failureCount, RenderFailureRetryBaseDelay, MaxRenderFailureRetryDelay);

    private bool TryConsumeTextureUploadBudget(long now)
    {
        lock (_textureUploadBudgetLock)
        {
            if (now >= _nextTextureUploadBudgetResetAtMs)
            {
                _nextTextureUploadBudgetResetAtMs = now + (long)TextureUploadBudgetWindow.TotalMilliseconds;
                _remainingTextureUploadsInWindow = MaxTextureUploadsPerBudgetWindow;
            }

            if (_remainingTextureUploadsInWindow <= 0)
            {
                return false;
            }

            _remainingTextureUploadsInWindow--;
            return true;
        }
    }

    private static void MarkGeometryAccess(SharedGeometryCacheEntry entry, ObjectCatalogPreviewMode mode, long now)
    {
        lock (entry.SyncRoot)
        {
            if (mode == ObjectCatalogPreviewMode.Thumbnail)
            {
                entry.LastThumbnailAccessAtMs = now;
            }
            else
            {
                entry.LastDetailAccessAtMs = now;
            }
        }
    }

    private static void MarkPreviewAccess(PreviewAssetState assetState, PreviewRenderState renderState, long now)
    {
        renderState.LastAccessAtMs = now;
        switch (renderState.Mode)
        {
            case ObjectCatalogPreviewMode.Thumbnail:
                assetState.LastThumbnailAccessAtMs = now;
                break;
            default:
                assetState.LastDetailAccessAtMs = now;
                break;
        }
    }

    private void ApplyPendingTexture(PreviewRenderState renderState, ObjectCatalogPreviewRequest request, long now)
    {
        if (renderState.PendingTexture is null)
        {
            return;
        }

        if (IsThumbnailWorkExpired(renderState.Mode, renderState.LastAccessAtMs, now))
        {
            renderState.PendingTexture = null;
            return;
        }

        if (renderState.PendingTexture.Request != request)
        {
            renderState.PendingTexture = null;
            return;
        }

        if (!TryConsumeTextureUploadBudget(now))
        {
            return;
        }

        try
        {
            PendingPreviewTexture pendingTexture = renderState.PendingTexture;
            IDalamudTextureWrap nextTexture = _textureProvider.CreateFromRaw(
                RawImageSpecification.Rgba32(pendingTexture.Width, pendingTexture.Height),
                pendingTexture.Pixels,
                pendingTexture.TextureName);
            ReleaseRenderTexture(renderState);
            renderState.Texture = nextTexture;
            renderState.TextureByteCount = pendingTexture.ByteCount;
            renderState.LastRenderedRequest = request;
            renderState.HasRenderedRequest = true;
            renderState.LastRenderSucceeded = true;
            renderState.RenderFailureCount = 0;
            renderState.NextRenderRetryAtMs = 0;
            renderState.Error = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create object catalog preview texture for {Mode}", renderState.Mode);
            if (renderState.Texture is null)
            {
                renderState.LastRenderedRequest = request;
                renderState.HasRenderedRequest = true;
                renderState.LastRenderSucceeded = false;
                renderState.RenderFailureCount++;
                renderState.NextRenderRetryAtMs = now + GetRenderRetryDelayMilliseconds(renderState.RenderFailureCount);
                renderState.Error = "Failed to create preview texture.";
            }
        }
        finally
        {
            renderState.PendingTexture = null;
        }
    }

    private void EnsurePreviewWork(
        string assetKey,
        PreviewAssetState assetState,
        PreviewRenderState renderState,
        ObjectCatalogPreviewRequest request,
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

        if (!CanProcessRequestedRender(assetState, renderState, now))
        {
            return;
        }

        QueueRender(assetKey, renderState);
    }

    private void QueueLoad(string assetKey, PreviewAssetState assetState, long now)
    {
        if (assetState.LoadQueued || assetState.LoadRunning || assetState.HasLoadedGeometry || assetState.NextLoadRetryAtMs > now)
        {
            return;
        }

        assetState.LoadQueued = true;
        _loadQueue.Enqueue(assetKey);
        _loadSignal.Release();
    }

    private static bool TryMarkRenderQueued(PreviewRenderState renderState)
    {
        if (renderState.RenderQueued || renderState.RenderRunning || !renderState.HasRequestedRequest)
        {
            return false;
        }

        renderState.RenderQueued = true;
        return true;
    }

    private void QueueRender(string assetKey, PreviewRenderState renderState)
    {
        if (!TryMarkRenderQueued(renderState))
        {
            return;
        }

        QueueRenderWork(new RenderWorkItem(assetKey, renderState.Mode));
    }

    private void QueueRenderWork(RenderWorkItem workItem)
    {
        switch (workItem.Mode)
        {
            case ObjectCatalogPreviewMode.Thumbnail:
                _thumbnailRenderQueue.Enqueue(workItem);
                break;
            default:
                _detailRenderQueue.Enqueue(workItem);
                break;
        }

        _renderSignal.Release();
    }

    private void RunLoadWorker()
    {
        try
        {
            while (!_disposeCancellation.IsCancellationRequested)
            {
                _loadSignal.Wait(_disposeCancellation.Token);
                while (_loadQueue.TryDequeue(out string? assetKey))
                {
                    if (_disposeCancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    if (!TryGetAssetState(assetKey, out PreviewAssetState? state) || state is null)
                    {
                        continue;
                    }

                    lock (state.SyncRoot)
                    {
                        if (state.HasLoadedGeometry || state.LoadRunning || !state.LoadQueued)
                        {
                            continue;
                        }

                        state.LoadQueued = false;
                        if (!HasRecentLoadInterest(state, GetNowMilliseconds()))
                        {
                            continue;
                        }

                        state.LoadRunning = true;
                    }

                    LoadGeometry(assetKey, state);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ignored during shutdown
        }
    }

    private void RunRenderWorker()
    {
        try
        {
            while (!_disposeCancellation.IsCancellationRequested)
            {
                _renderSignal.Wait(_disposeCancellation.Token);
                while (TryDequeueRenderWork(out RenderWorkItem workItem))
                {
                    if (_disposeCancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    if (!TryGetAssetState(workItem.AssetKey, out PreviewAssetState? state) || state is null)
                    {
                        continue;
                    }

                    SharedGeometryCacheEntry? geometryEntry = null;
                    ObjectCatalogPreviewRequest request;
                    CancellationTokenSource cancellation;
                    lock (state.SyncRoot)
                    {
                        if (!state.RenderStates.TryGetValue(workItem.Mode, out PreviewRenderState? renderState))
                        {
                            continue;
                        }

                        renderState.RenderQueued = false;
                        if (!CanProcessRequestedRender(state, renderState, GetNowMilliseconds()))
                        {
                            continue;
                        }

                        cancellation = new CancellationTokenSource();
                        renderState.RenderCancellation?.Dispose();
                        renderState.RenderCancellation = cancellation;
                        renderState.ActiveRenderRequest = renderState.RequestedRequest;
                        renderState.HasActiveRenderRequest = true;
                        renderState.RenderRunning = true;
                        request = renderState.ActiveRenderRequest;
                        geometryEntry = state.GeometryEntry;
                        if (geometryEntry is null)
                        {
                            continue;
                        }
                    }

                    using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        _disposeCancellation.Token,
                        cancellation.Token);
                    RenderPreview(workItem.AssetKey, state, workItem.Mode, geometryEntry, request, linkedCancellation.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ignored during shutdown
        }
    }

    private bool TryDequeueRenderWork(out RenderWorkItem workItem)
    {
        bool preferThumbnail = (Interlocked.Increment(ref _renderDequeueCounter) & 1) == 0;
        return preferThumbnail
            ? _thumbnailRenderQueue.TryDequeue(out workItem) || _detailRenderQueue.TryDequeue(out workItem)
            : _detailRenderQueue.TryDequeue(out workItem) || _thumbnailRenderQueue.TryDequeue(out workItem);
    }

    private void LoadGeometry(string assetKey, PreviewAssetState state)
    {
        string? error = null;
        SharedGeometryCacheEntry? geometryEntry = null;

        try
        {
            geometryEntry = TryGetOrBuildSharedGeometryEntry(state, out error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load object catalog preview geometry for {Kind} {Path}", state.Kind, state.Path);
            error = "Failed to load preview geometry.";
        }

        long now = GetNowMilliseconds();
        List<RenderWorkItem> requeueItems = [];
        SharedGeometryCacheEntry? attachedGeometryEntry = geometryEntry;
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
                state.NextLoadRetryAtMs = now + GetLoadRetryDelayMilliseconds(state.LoadFailureCount);
            }
            lastThumbnailAccessAtMs = state.LastThumbnailAccessAtMs;
            lastDetailAccessAtMs = state.LastDetailAccessAtMs;

            if (state.GeometryEntry is not null)
            {
                requeueItems = CollectPostLoadRenderWork(assetKey, state, now);
            }
        }

        if (attachedGeometryEntry is not null)
        {
            SyncGeometryAccessFromState(attachedGeometryEntry, lastThumbnailAccessAtMs, lastDetailAccessAtMs);
        }

        foreach (RenderWorkItem requeueItem in requeueItems)
        {
            QueueRenderWork(requeueItem);
        }
    }

    private void RenderPreview(
        string assetKey,
        PreviewAssetState state,
        ObjectCatalogPreviewMode mode,
        SharedGeometryCacheEntry geometryEntry,
        ObjectCatalogPreviewRequest request,
        CancellationToken cancellationToken)
    {
        PendingPreviewTexture? pendingTexture = null;
        string? error = null;
        var wasCanceled = false;

        try
        {
            BeginGeometryRender(geometryEntry, mode, GetNowMilliseconds());
            byte[] pixels = PreviewRenderUtility.Render(
                geometryEntry.Geometry,
                state.Kind,
                request.ToCameraState(),
                request.Width,
                request.Height,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            pendingTexture = new PendingPreviewTexture(
                request,
                pixels,
                request.Width,
                request.Height,
                $"Objects.Preview.{mode}.{state.Kind}.{Path.GetFileNameWithoutExtension(state.Path)}");
        }
        catch (OperationCanceledException)
        {
            wasCanceled = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to render object catalog preview for {Mode} {Kind} {Path}", mode, state.Kind, state.Path);
            error = "Failed to render preview.";
        }
        finally
        {
            EndGeometryRender(geometryEntry);
        }

        var now = GetNowMilliseconds();
        bool shouldRequeue = false;
        lock (state.SyncRoot)
        {
            if (!state.RenderStates.TryGetValue(mode, out PreviewRenderState? renderState))
            {
                return;
            }

            renderState.RenderRunning = false;
            renderState.HasActiveRenderRequest = false;
            renderState.RenderCancellation?.Dispose();
            renderState.RenderCancellation = null;

            if (!wasCanceled && renderState.HasRequestedRequest && renderState.RequestedRequest == request)
            {
                renderState.LastRenderedRequest = request;
                renderState.HasRenderedRequest = true;
                if (pendingTexture is not null
                 && !IsThumbnailWorkExpired(mode, renderState.LastAccessAtMs, now))
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
                    renderState.NextRenderRetryAtMs = now + GetRenderRetryDelayMilliseconds(renderState.RenderFailureCount);
                    renderState.Error = error ?? "Failed to render preview.";
                }
            }

            if (mode == renderState.Mode
             && CanProcessRequestedRender(state, renderState, now)
             && TryMarkRenderQueued(renderState))
            {
                shouldRequeue = true;
            }
        }

        if (shouldRequeue)
        {
            QueueRenderWork(new RenderWorkItem(assetKey, mode));
        }
    }

    private IReadOnlyList<PreviewModelInfo> ResolvePreviewModels(ObjectCatalogKind kind, string path)
    {
        IReadOnlyList<PreviewModelInfo> previewModels = _objectCatalog.ResolvePreviewModels(kind, path);
        if (previewModels.Count > 0)
        {
            return previewModels;
        }

        if (kind == ObjectCatalogKind.Furniture && ObjectPathRules.IsCatalogSharedGroupPath(path))
        {
            return SharedGroupAssetResolver.AnalyzeSharedGroup(_objectAssetGameData, path).PreviewModels;
        }

        return [];
    }

    private bool TryLoadPreviewGeometry(
        string modelPath,
        IDictionary<string, MdlDecimator.PreviewGeometry> loadedGeometry,
        out MdlDecimator.PreviewGeometry geometry,
        out string? reason)
    {
        reason = null;
        if (loadedGeometry.TryGetValue(modelPath, out geometry!))
        {
            return true;
        }

        var file = _gameData.GetFile(modelPath);
        if (file is null)
        {
            geometry = null!;
            reason = $"Model file could not be loaded: {modelPath}";
            return false;
        }

        if (!MdlDecimator.TryLoadPreviewGeometry(file.Data, out geometry, out reason))
        {
            return false;
        }

        loadedGeometry[modelPath] = geometry;
        return true;
    }

    private static void AppendPreviewPositions(
        List<Vector3> mergedPositions,
        IReadOnlyList<Vector3> positions,
        Matrix4x4 transform)
    {
        if (transform == Matrix4x4.Identity)
        {
            mergedPositions.AddRange(positions);
            return;
        }

        for (var i = 0; i < positions.Count; i++)
        {
            mergedPositions.Add(Vector3.Transform(positions[i], transform));
        }
    }

    private SharedGeometryCacheEntry? TryGetOrBuildSharedGeometryEntry(PreviewAssetState state, out string? error)
    {
        IReadOnlyList<PreviewModelInfo> previewModels = ResolvePreviewModels(state.Kind, state.Path);
        if (previewModels.Count == 0)
        {
            error = "Preview unavailable for this asset.";
            return null;
        }

        string cacheKey = BuildMergedGeometryKey(previewModels);
        if (TryGetSharedGeometryEntry(cacheKey, out SharedGeometryCacheEntry? sharedEntry))
        {
            error = null;
            return sharedEntry;
        }

        MdlDecimator.PreviewGeometry? geometry = BuildMergedGeometry(previewModels, out error);
        if (geometry is null)
        {
            return null;
        }

        return GetOrCreateSharedGeometryEntry(cacheKey, geometry);
    }

    private static string BuildMergedGeometryKey(IReadOnlyList<PreviewModelInfo> previewModels)
    {
        StringBuilder builder = new();
        for (var i = 0; i < previewModels.Count; i++)
        {
            PreviewModelInfo previewModel = previewModels[i];
            if (i > 0)
            {
                builder.Append('|');
            }

            builder.Append(ObjectPathRules.NormalizeGamePath(previewModel.ModelPath));
            builder.Append('@');
            AppendMatrixKey(builder, previewModel.Transform);
        }

        return builder.ToString();
    }

    private static void AppendMatrixKey(StringBuilder builder, Matrix4x4 matrix)
    {
        AppendFloatKey(builder, matrix.M11);
        AppendFloatKey(builder, matrix.M12);
        AppendFloatKey(builder, matrix.M13);
        AppendFloatKey(builder, matrix.M14);
        AppendFloatKey(builder, matrix.M21);
        AppendFloatKey(builder, matrix.M22);
        AppendFloatKey(builder, matrix.M23);
        AppendFloatKey(builder, matrix.M24);
        AppendFloatKey(builder, matrix.M31);
        AppendFloatKey(builder, matrix.M32);
        AppendFloatKey(builder, matrix.M33);
        AppendFloatKey(builder, matrix.M34);
        AppendFloatKey(builder, matrix.M41);
        AppendFloatKey(builder, matrix.M42);
        AppendFloatKey(builder, matrix.M43);
        AppendFloatKey(builder, matrix.M44);
    }

    private static void AppendFloatKey(StringBuilder builder, float value)
    {
        builder.Append(BitConverter.SingleToInt32Bits(value).ToString("X8", CultureInfo.InvariantCulture));
        builder.Append(',');
    }

    private bool TryGetSharedGeometryEntry(string cacheKey, out SharedGeometryCacheEntry? entry)
    {
        lock (_lock)
        {
            return _sharedGeometryCache.TryGetValue(cacheKey, out entry);
        }
    }

    private SharedGeometryCacheEntry GetOrCreateSharedGeometryEntry(string cacheKey, MdlDecimator.PreviewGeometry geometry)
    {
        lock (_lock)
        {
            if (_sharedGeometryCache.TryGetValue(cacheKey, out SharedGeometryCacheEntry? existingEntry))
            {
                return existingEntry;
            }

            SharedGeometryCacheEntry entry = new(cacheKey, geometry);
            _sharedGeometryCache.Add(cacheKey, entry);
            return entry;
        }
    }

    private MdlDecimator.PreviewGeometry? BuildMergedGeometry(IReadOnlyList<PreviewModelInfo> previewModels, out string? error)
    {
        List<Vector3> mergedPositions = [];
        List<Vector2> mergedTexCoords = [];
        List<int> mergedIndices = [];
        List<int> mergedTriangleMaterialIndices = [];
        List<MdlDecimator.PreviewMaterial> mergedMaterials = [];
        Dictionary<string, MdlDecimator.PreviewTexture?> loadedTextures = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, MdlDecimator.PreviewGeometry> loadedGeometry = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, MdlDecimator.PreviewMaterial[]> resolvedMaterialCache = new(StringComparer.OrdinalIgnoreCase);
        string? firstReason = null;

        for (var i = 0; i < previewModels.Count; i++)
        {
            PreviewModelInfo previewModel = previewModels[i];
            if (!TryAppendMergedPreviewModel(
                    previewModel,
                    mergedPositions,
                    mergedTexCoords,
                    mergedIndices,
                    mergedTriangleMaterialIndices,
                    mergedMaterials,
                    loadedTextures,
                    loadedGeometry,
                    resolvedMaterialCache,
                    out string? loadReason))
            {
                firstReason ??= loadReason ?? $"Model file could not be loaded: {previewModel.ModelPath}";
            }
        }

        if (mergedPositions.Count == 0 || mergedIndices.Count < 3)
        {
            error = firstReason ?? "Preview geometry could not be loaded.";
            return null;
        }

        error = null;
        return new MdlDecimator.PreviewGeometry(
            mergedPositions.ToArray(),
            mergedTexCoords.ToArray(),
            mergedIndices.ToArray(),
            mergedTriangleMaterialIndices.ToArray(),
            mergedMaterials.ToArray());
    }

    private bool TryAppendMergedPreviewModel(
        PreviewModelInfo previewModel,
        List<Vector3> mergedPositions,
        List<Vector2> mergedTexCoords,
        List<int> mergedIndices,
        List<int> mergedTriangleMaterialIndices,
        List<MdlDecimator.PreviewMaterial> mergedMaterials,
        IDictionary<string, MdlDecimator.PreviewTexture?> loadedTextures,
        IDictionary<string, MdlDecimator.PreviewGeometry> loadedGeometry,
        IDictionary<string, MdlDecimator.PreviewMaterial[]> resolvedMaterialCache,
        out string? reason)
    {
        reason = null;
        if (!TryLoadPreviewGeometry(previewModel.ModelPath, loadedGeometry, out MdlDecimator.PreviewGeometry modelGeometry, out reason))
        {
            return false;
        }

        if (!resolvedMaterialCache.TryGetValue(previewModel.ModelPath, out MdlDecimator.PreviewMaterial[]? resolvedMaterials))
        {
            resolvedMaterials = ResolvePreviewMaterials(previewModel.ModelPath, modelGeometry.Materials, loadedTextures);
            resolvedMaterialCache[previewModel.ModelPath] = resolvedMaterials;
        }

        int baseVertexIndex = mergedPositions.Count;
        int baseMaterialIndex = mergedMaterials.Count;
        AppendPreviewPositions(mergedPositions, modelGeometry.Positions, previewModel.Transform);
        mergedTexCoords.AddRange(modelGeometry.TexCoords);

        for (var index = 0; index < modelGeometry.Indices.Length; index++)
        {
            mergedIndices.Add(baseVertexIndex + modelGeometry.Indices[index]);
        }

        for (var triangleIndex = 0; triangleIndex < modelGeometry.TriangleMaterialIndices.Length; triangleIndex++)
        {
            int materialIndex = modelGeometry.TriangleMaterialIndices[triangleIndex];
            mergedTriangleMaterialIndices.Add(materialIndex >= 0 ? baseMaterialIndex + materialIndex : -1);
        }

        mergedMaterials.AddRange(resolvedMaterials);
        return true;
    }

    private static void SyncGeometryAccessFromState(SharedGeometryCacheEntry entry, long lastThumbnailAccessAtMs, long lastDetailAccessAtMs)
    {
        if (lastThumbnailAccessAtMs > 0)
        {
            MarkGeometryAccess(entry, ObjectCatalogPreviewMode.Thumbnail, lastThumbnailAccessAtMs);
        }

        if (lastDetailAccessAtMs > 0)
        {
            MarkGeometryAccess(entry, ObjectCatalogPreviewMode.Detail, lastDetailAccessAtMs);
        }
    }

    private static List<RenderWorkItem> CollectPostLoadRenderWork(string assetKey, PreviewAssetState state, long now)
    {
        List<RenderWorkItem> requeueItems = [];
        foreach ((ObjectCatalogPreviewMode mode, PreviewRenderState renderState) in state.RenderStates)
        {
            if (mode != renderState.Mode
             || !CanProcessRequestedRender(state, renderState, now)
             || !TryMarkRenderQueued(renderState))
            {
                continue;
            }

            requeueItems.Add(new RenderWorkItem(assetKey, mode));
        }

        return requeueItems;
    }

    private MdlDecimator.PreviewMaterial[] ResolvePreviewMaterials(
        string modelPath,
        IReadOnlyList<MdlDecimator.PreviewMaterial> materials,
        IDictionary<string, MdlDecimator.PreviewTexture?> loadedTextures)
    {
        MdlDecimator.PreviewMaterial[] resolvedMaterials = new MdlDecimator.PreviewMaterial[materials.Count];
        for (var i = 0; i < materials.Count; i++)
        {
            string materialName = materials[i].Name;
            if (!TryResolveMaterialPath(modelPath, materialName, out string materialPath)
             || !TryLoadMaterial(materialPath, out PenumbraMtrlFile material)
             || !TextureMapKindResolver.TryGetTexturePath(material, TextureMapKind.Diffuse, out string texturePath))
            {
                resolvedMaterials[i] = new MdlDecimator.PreviewMaterial(materialName, null, null, false, false, 1f);
                continue;
            }

            string normalizedTexturePath = ObjectPathRules.NormalizeGamePath(texturePath);
            if (!loadedTextures.TryGetValue(texturePath, out MdlDecimator.PreviewTexture? diffuseTexture))
            {
                diffuseTexture = TryLoadPreviewTexture(texturePath);
                loadedTextures[texturePath] = diffuseTexture;
            }

            resolvedMaterials[i] = new MdlDecimator.PreviewMaterial(
                materialPath,
                string.IsNullOrWhiteSpace(normalizedTexturePath) ? null : normalizedTexturePath,
                diffuseTexture,
                ShouldApplyAlphaClip(material),
                IsTransparent(material),
                GetTransparency(material));
        }

        return resolvedMaterials;
    }

    private static bool IsTransparent(PenumbraMtrlFile material)
        => new ShaderFlags(material.ShaderPackage.Flags).EnableTransparency;

    private static bool ShouldApplyAlphaClip(PenumbraMtrlFile material)
    {
        const float AlphaThresholdEpsilon = 0.001f;

        foreach (var constant in material.ShaderPackage.Constants)
        {
            var constantName = ShaderNames.TryResolve(ShaderNames.KnownNames, constant.Id);
            if (constantName != "g_AlphaThreshold" && constantName != "g_ShadowAlphaThreshold")
            {
                continue;
            }

            ReadOnlySpan<float> values = material.GetConstantValue<float>(constant);
            if (values.IsEmpty)
            {
                continue;
            }

            for (var valueIndex = 0; valueIndex < values.Length; valueIndex++)
            {
                if (values[valueIndex] > AlphaThresholdEpsilon)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static float GetTransparency(PenumbraMtrlFile material)
    {
        const float DefaultTransparency = 1f;
        const float TransparencyEpsilon = 0.001f;

        foreach (var constant in material.ShaderPackage.Constants)
        {
            var constantName = ShaderNames.TryResolve(ShaderNames.KnownNames, constant.Id);
            if (constantName != "g_Transparency")
            {
                continue;
            }

            ReadOnlySpan<float> values = material.GetConstantValue<float>(constant);
            if (values.IsEmpty)
            {
                continue;
            }

            float transparency = values[0];
            if (transparency > TransparencyEpsilon)
            {
                return Math.Clamp(transparency, 0f, 1f);
            }
        }

        return DefaultTransparency;
    }

    private bool TryLoadMaterial(string materialPath, out PenumbraMtrlFile material)
    {
        material = null!;

        try
        {
            var gameFile = _gameData.GetFile(materialPath);
            if (gameFile is null)
            {
                return false;
            }

            material = new PenumbraMtrlFile(gameFile.Data);
            return material.Valid;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load preview material {MaterialPath}", materialPath);
            return false;
        }
    }

    private MdlDecimator.PreviewTexture? TryLoadPreviewTexture(string texturePath)
    {
        string normalizedTexturePath = ObjectPathRules.NormalizeGamePath(texturePath);
        if (string.IsNullOrWhiteSpace(normalizedTexturePath))
        {
            return null;
        }

        long now = GetNowMilliseconds();
        lock (_previewTextureCacheLock)
        {
            if (_previewTextureCache.TryGetValue(normalizedTexturePath, out CachedPreviewTextureState? cachedTexture))
            {
                cachedTexture.LastAccessAtMs = now;
                return cachedTexture.Texture;
            }
        }

        MdlDecimator.PreviewTexture? previewTexture = null;
        try
        {
            TexFile? texFile = _gameData.GetFile<TexFile>(normalizedTexturePath);
            if (texFile is not null)
            {
                int width = texFile.Header.Width;
                int height = texFile.Header.Height;
                byte[] pixels = texFile.GetRgbaImageData();
                if (pixels.Length >= width * height * 4)
                {
                    if (Math.Max(width, height) > MaxPreviewTextureDimension)
                    {
                        float scale = MaxPreviewTextureDimension / (float)Math.Max(width, height);
                        int targetWidth = Math.Max(1, (int)MathF.Round(width * scale));
                        int targetHeight = Math.Max(1, (int)MathF.Round(height * scale));
                        pixels = DownscaleRgba(pixels, width, height, targetWidth, targetHeight);
                        width = targetWidth;
                        height = targetHeight;
                    }

                    previewTexture = new MdlDecimator.PreviewTexture(pixels, width, height);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load preview texture {TexturePath}", normalizedTexturePath);
        }

        lock (_previewTextureCacheLock)
        {
            _previewTextureCache[normalizedTexturePath] = new CachedPreviewTextureState(
                previewTexture,
                previewTexture?.RgbaPixels.LongLength ?? 0,
                now);
        }

        return previewTexture;
    }

    private bool TryResolveMaterialPath(string modelPath, string materialName, out string materialPath)
    {
        materialPath = string.Empty;

        string normalizedMaterialName = ObjectPathRules.NormalizeGamePath(materialName);
        if (string.IsNullOrWhiteSpace(normalizedMaterialName))
        {
            return false;
        }

        if (!ObjectPathRules.IsMaterialPath(normalizedMaterialName))
        {
            normalizedMaterialName += ".mtrl";
        }

        string cacheKey = $"{ObjectPathRules.NormalizeGamePath(modelPath)}|{normalizedMaterialName}";
        lock (_materialPathCacheLock)
        {
            if (_materialPathCache.TryGetValue(cacheKey, out string? cachedPath))
            {
                materialPath = cachedPath ?? string.Empty;
                return cachedPath is not null;
            }
        }

        if (ObjectMaterialPathUtility.TryResolveMaterialPath(_gameData, modelPath, normalizedMaterialName, out string resolvedMaterialPath))
        {
            materialPath = resolvedMaterialPath;
            lock (_materialPathCacheLock)
            {
                _materialPathCache[cacheKey] = resolvedMaterialPath;
            }
            return true;
        }

        lock (_materialPathCacheLock)
        {
            _materialPathCache[cacheKey] = null;
        }

        return false;
    }

    private void MaybeTrimCaches(long now)
    {
        if (now < Interlocked.Read(ref _nextCleanupAtMs))
        {
            return;
        }

        lock (_lock)
        {
            if (now < _nextCleanupAtMs)
            {
                return;
            }

            _nextCleanupAtMs = now + (long)CleanupInterval.TotalMilliseconds;
            TrimRenderCaches(now);
        }

        TrimDecodedTextureCache(now);
    }

    private void TrimRenderCaches(long now)
    {
        List<PreviewRenderState> thumbnailCandidates = [];
        List<PreviewRenderState> detailCandidates = [];
        List<string> removableAssetKeys = [];

        foreach ((string assetKey, PreviewAssetState state) in _states)
        {
            lock (state.SyncRoot)
            {
                foreach (PreviewRenderState renderState in state.RenderStates.Values)
                {
                    TrimPendingTexture(renderState, now);

                    if (renderState.Texture is null && renderState.PendingTexture is null)
                    {
                        continue;
                    }

                    switch (renderState.Mode)
                    {
                        case ObjectCatalogPreviewMode.Thumbnail:
                            thumbnailCandidates.Add(renderState);
                            break;
                        default:
                            detailCandidates.Add(renderState);
                            break;
                    }
                }

                if (ShouldRemoveAssetState(state, now))
                {
                    removableAssetKeys.Add(assetKey);
                }
            }
        }

        TrimModeTextures(
            thumbnailCandidates,
            now,
            ThumbnailPolicy);
        TrimModeTextures(
            detailCandidates,
            now,
            DetailPolicy);
        TrimSharedGeometryMaterialTextures(now);
        TrimSharedGeometryCache(now);

        foreach (string assetKey in removableAssetKeys)
        {
            if (!_states.TryGetValue(assetKey, out PreviewAssetState? state))
            {
                continue;
            }

            lock (state.SyncRoot)
            {
                if (!ShouldRemoveAssetState(state, now))
                {
                    continue;
                }

                foreach (PreviewRenderState renderState in state.RenderStates.Values)
                {
                    DisposeRenderState(renderState);
                }

                state.RenderStates.Clear();
                state.GeometryEntry = null;
            }

            _states.Remove(assetKey);
        }
    }

    private static void TrimPendingTexture(PreviewRenderState renderState, long now)
    {
        if (renderState.PendingTexture is null)
        {
            return;
        }

        PreviewModePolicy policy = GetPolicy(renderState.Mode);
        if (!IsExpired(renderState.LastAccessAtMs, now, policy.PendingTextureRetention))
        {
            return;
        }

        renderState.PendingTexture = null;
    }

    private static void TrimModeTextures(
        List<PreviewRenderState> renderStates,
        long now,
        PreviewModePolicy policy)
    {
        if (renderStates.Count == 0)
        {
            return;
        }

        renderStates.Sort(static (left, right) => left.LastAccessAtMs.CompareTo(right.LastAccessAtMs));
        long totalBytes = 0;
        int remainingCount = 0;
        foreach (PreviewRenderState renderState in renderStates)
        {
            if (!HasRetainedTexture(renderState))
            {
                continue;
            }

            totalBytes += GetRetainedTextureByteCount(renderState);
            remainingCount++;
        }

        foreach (PreviewRenderState renderState in renderStates)
        {
            if (!HasRetainedTexture(renderState))
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

            totalBytes -= GetRetainedTextureByteCount(renderState);
            remainingCount--;
            ReleaseRetainedTextures(renderState);
        }
    }

    private static bool ShouldRemoveAssetState(PreviewAssetState state, long now)
    {
        if (state.LoadQueued || state.LoadRunning)
        {
            return false;
        }

        if (state.RenderStates.Values.Any(HasActiveRenderResources))
        {
            return false;
        }

        return IsExpired(state.LastThumbnailAccessAtMs, now, ThumbnailPolicy.AssetRetention)
            && IsExpired(state.LastDetailAccessAtMs, now, DetailPolicy.AssetRetention);
    }

    private static bool CanTrimSharedGeometryEntry(SharedGeometryCacheEntry entry, long now)
        => entry.ActiveRenderCount == 0
            && IsExpired(entry.LastThumbnailAccessAtMs, now, ThumbnailPolicy.PendingTextureRetention)
            && IsExpired(entry.LastDetailAccessAtMs, now, DetailPolicy.PendingTextureRetention);

    private void TrimSharedGeometryMaterialTextures(long now)
    {
        foreach (SharedGeometryCacheEntry entry in _sharedGeometryCache.Values)
        {
            lock (entry.SyncRoot)
            {
                if (entry.ActiveRenderCount > 0
                 || !IsExpired(entry.LastThumbnailAccessAtMs, now, ThumbnailPolicy.TextureRetention)
                 || !IsExpired(entry.LastDetailAccessAtMs, now, DetailPolicy.TextureRetention))
                {
                    continue;
                }

                ReleaseGeometryMaterialTextures(entry.Geometry);
            }
        }
    }

    private void TrimSharedGeometryCache(long now)
    {
        long totalGeometryBytes = 0;
        foreach (SharedGeometryCacheEntry entry in _sharedGeometryCache.Values)
        {
            totalGeometryBytes += entry.GeometryByteCount;
        }

        if (_sharedGeometryCache.Count <= MaxThumbnailGeometryCount
         && totalGeometryBytes <= MaxThumbnailGeometryBytes)
        {
            return;
        }

        List<SharedGeometryCacheEntry> candidates = _sharedGeometryCache.Values
            .Where(entry => CanTrimSharedGeometryEntry(entry, now))
            .OrderBy(static entry => Math.Max(entry.LastThumbnailAccessAtMs, entry.LastDetailAccessAtMs))
            .ToList();
        foreach (SharedGeometryCacheEntry candidate in candidates)
        {
            if (_sharedGeometryCache.Count <= MaxThumbnailGeometryCount
             && totalGeometryBytes <= MaxThumbnailGeometryBytes)
            {
                break;
            }

            lock (candidate.SyncRoot)
            {
                if (!CanTrimSharedGeometryEntry(candidate, now))
                {
                    continue;
                }

                ReleaseGeometryMaterialTextures(candidate.Geometry);
            }

            if (_sharedGeometryCache.Remove(candidate.CacheKey))
            {
                totalGeometryBytes -= candidate.GeometryByteCount;
                DetachSharedGeometryEntryFromStates(candidate);
            }
        }
    }

    private void DetachSharedGeometryEntryFromStates(SharedGeometryCacheEntry entry)
    {
        foreach (PreviewAssetState state in _states.Values)
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

    private void TrimDecodedTextureCache(long now)
    {
        lock (_previewTextureCacheLock)
        {
            if (_previewTextureCache.Count == 0)
            {
                return;
            }

            List<KeyValuePair<string, CachedPreviewTextureState>> candidates = new(_previewTextureCache);
            candidates.Sort(static (left, right) => left.Value.LastAccessAtMs.CompareTo(right.Value.LastAccessAtMs));

            long totalBytes = 0;
            foreach ((_, CachedPreviewTextureState cacheEntry) in candidates)
            {
                totalBytes += cacheEntry.ByteCount;
            }

            foreach ((string key, CachedPreviewTextureState cacheEntry) in candidates)
            {
                bool shouldRemove = IsExpired(cacheEntry.LastAccessAtMs, now, PreviewTextureCacheRetention)
                    || totalBytes > MaxPreviewTextureCacheBytes;
                if (!shouldRemove)
                {
                    continue;
                }

                totalBytes -= cacheEntry.ByteCount;
                _previewTextureCache.Remove(key);
            }
        }
    }

    private static void ReleaseRenderTexture(PreviewRenderState renderState)
    {
        renderState.Texture?.Dispose();
        renderState.Texture = null;
        renderState.TextureByteCount = 0;
    }

    private void BeginGeometryRender(SharedGeometryCacheEntry entry, ObjectCatalogPreviewMode mode, long now)
    {
        lock (entry.SyncRoot)
        {
            entry.ActiveRenderCount++;
            if (mode == ObjectCatalogPreviewMode.Thumbnail)
            {
                entry.LastThumbnailAccessAtMs = now;
            }
            else
            {
                entry.LastDetailAccessAtMs = now;
            }

            EnsureGeometryMaterialTextures(entry.Geometry);
        }
    }

    private static void EndGeometryRender(SharedGeometryCacheEntry entry)
    {
        lock (entry.SyncRoot)
        {
            if (entry.ActiveRenderCount > 0)
            {
                entry.ActiveRenderCount--;
            }
        }
    }

    private void EnsureGeometryMaterialTextures(MdlDecimator.PreviewGeometry geometry)
    {
        foreach (MdlDecimator.PreviewMaterial material in geometry.Materials)
        {
            if (material.DiffuseTexture is not null || string.IsNullOrWhiteSpace(material.DiffuseTexturePath))
            {
                continue;
            }

            material.DiffuseTexture = TryLoadPreviewTexture(material.DiffuseTexturePath);
        }
    }

    private static void ReleaseGeometryMaterialTextures(MdlDecimator.PreviewGeometry geometry)
    {
        foreach (MdlDecimator.PreviewMaterial material in geometry.Materials)
        {
            material.DiffuseTexture = null;
        }
    }

    private static bool HasRetainedTexture(PreviewRenderState renderState)
        => renderState.Texture is not null || renderState.PendingTexture is not null;

    private static long GetRetainedTextureByteCount(PreviewRenderState renderState)
        => renderState.TextureByteCount + (renderState.PendingTexture?.ByteCount ?? 0);

    private static void ReleaseRetainedTextures(PreviewRenderState renderState)
    {
        ReleaseRenderTexture(renderState);
        renderState.PendingTexture = null;
    }

    private static void DisposeRenderState(PreviewRenderState renderState)
    {
        renderState.RenderCancellation?.Cancel();
        renderState.RenderCancellation?.Dispose();
        renderState.RenderCancellation = null;
        ReleaseRetainedTextures(renderState);
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
}
