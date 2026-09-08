using Dalamud.Plugin.Services;
using Intoner.Objects.Assets;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Runtime;

internal sealed class ObjectModelSelectionGeometryCache : ISceneSelectionGeometryProvider, IDisposable
{
    private const int BuildShutdownWaitMilliseconds = 2000;
    private static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(5);

    private sealed class CacheEntry
    {
        public SceneSelectionGeometry? Geometry { get; init; }
        public DateTime LastTouchedUtc { get; set; }
        public bool AccelerationRequested { get; set; }
    }

    private readonly ILogger<ObjectModelSelectionGeometryCache> _logger;
    private readonly IDataManager _gameData;
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentExclusiveSchedulerPair _accelerationBuildScheduler = new(
        TaskScheduler.Default,
        maxConcurrencyLevel: 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly CancellationToken _disposeToken;

    private int _disposed;

    public ObjectModelSelectionGeometryCache(
        ILogger<ObjectModelSelectionGeometryCache> logger,
        IDataManager gameData)
    {
        _logger = logger;
        _gameData = gameData;
        _disposeToken = _disposeCancellation.Token;
    }

    public bool TryGetGeometry(string modelPath, out SceneSelectionGeometry geometry)
        => TryGetGeometryCore(modelPath, requestAcceleration: false, out geometry);

    public bool TryGetRaycastGeometry(string modelPath, out SceneSelectionGeometry geometry)
        => TryGetGeometryCore(modelPath, requestAcceleration: true, out geometry);

    private bool TryGetGeometryCore(
        string modelPath,
        bool requestAcceleration,
        out SceneSelectionGeometry geometry)
    {
        geometry = null!;
        if (Volatile.Read(ref _disposed) != 0
            || !ObjectResourcePathUtility.TryNormalizeTrackedPath(modelPath, out var normalizedPath))
        {
            return false;
        }

        CacheEntry cacheEntry = TryGetCachedEntry(normalizedPath, out CacheEntry cachedEntry)
            ? cachedEntry
            : StoreEntry(normalizedPath, LoadGeometryEntry(normalizedPath, DateTime.UtcNow));
        if (requestAcceleration)
        {
            RequestAcceleration(normalizedPath, cacheEntry);
        }

        geometry = cacheEntry.Geometry!;
        return geometry != null;
    }

    public void Touch(string modelPath)
    {
        if (Volatile.Read(ref _disposed) != 0
            || !ObjectResourcePathUtility.TryNormalizeTrackedPath(modelPath, out var normalizedPath))
        {
            return;
        }

        lock (_cacheLock)
        {
            var now = DateTime.UtcNow;
            TrimExpiredUnsafe(now);
            if (_cache.TryGetValue(normalizedPath, out var entry))
            {
                entry.LastTouchedUtc = now;
            }
        }
    }

    private bool TryGetCachedEntry(string normalizedPath, out CacheEntry cacheEntry)
    {
        var now = DateTime.UtcNow;
        lock (_cacheLock)
        {
            TrimExpiredUnsafe(now);
            if (_cache.TryGetValue(normalizedPath, out var cachedEntry))
            {
                cacheEntry = cachedEntry;
                return true;
            }
        }

        cacheEntry = null!;
        return false;
    }

    private CacheEntry StoreEntry(string normalizedPath, CacheEntry cacheEntry)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(normalizedPath, out CacheEntry? storedEntry))
            {
                return storedEntry;
            }

            _cache[normalizedPath] = cacheEntry;
            return cacheEntry;
        }
    }

    private void RequestAcceleration(string normalizedPath, CacheEntry cacheEntry)
    {
        SceneSelectionGeometry geometry;
        lock (_cacheLock)
        {
            if (cacheEntry.AccelerationRequested || cacheEntry.Geometry is not { } cachedGeometry)
            {
                return;
            }

            cacheEntry.AccelerationRequested = true;
            geometry = cachedGeometry;
        }

        QueueAccelerationBuild(normalizedPath, geometry);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposeCancellation.Cancel();
        _accelerationBuildScheduler.Complete();
        try
        {
            _accelerationBuildScheduler.Completion.Wait(BuildShutdownWaitMilliseconds);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(
                   static inner => inner is OperationCanceledException))
        {
            // cancellation is expected during disposal
        }

        _disposeCancellation.Dispose();
    }

    private void QueueAccelerationBuild(string normalizedPath, SceneSelectionGeometry geometry)
    {
        CancellationToken cancellationToken = _disposeToken;
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            _ = Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        geometry.BuildAcceleration(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // cancellation is expected during disposal
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "object selection geometry acceleration failed for {ModelPath}", normalizedPath);
                    }
                },
                cancellationToken,
                TaskCreationOptions.DenyChildAttach,
                _accelerationBuildScheduler.ConcurrentScheduler);
        }
        catch (TaskSchedulerException) when (Volatile.Read(ref _disposed) != 0)
        {
            // disposal completed the scheduler before this work could be queued
        }
    }

    private CacheEntry LoadGeometryEntry(string normalizedPath, DateTime touchedUtc)
    {
        try
        {
            if (ObjectLocalFilePathUtility.IsLocalFilePath(normalizedPath))
            {
                return LoadLocalGeometryEntry(normalizedPath, touchedUtc);
            }

            var file = _gameData.GetFile(normalizedPath);
            if (file is null)
            {
                _logger.LogDebug("object selection geometry missing file {ModelPath}", normalizedPath);
                return new CacheEntry
                {
                    LastTouchedUtc = touchedUtc,
                };
            }

            if (!ModelPreviewGeometryReader.TryLoad(file.Data, out var previewGeometry, out var reason))
            {
                return CreateDecodeFailedEntry(normalizedPath, reason, touchedUtc);
            }

            return CreateGeometryEntry(previewGeometry, touchedUtc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "object selection geometry load failed for {ModelPath}", normalizedPath);
            return new CacheEntry
            {
                LastTouchedUtc = touchedUtc,
            };
        }
    }

    private CacheEntry LoadLocalGeometryEntry(string normalizedPath, DateTime touchedUtc)
    {
        string fileSystemPath = ObjectLocalFilePathUtility.ToFileSystemPath(normalizedPath);
        if (!File.Exists(fileSystemPath))
        {
            _logger.LogDebug("object selection geometry missing local file {ModelPath}", normalizedPath);
            return new CacheEntry
            {
                LastTouchedUtc = touchedUtc,
            };
        }

        var data = File.ReadAllBytes(fileSystemPath);
        return ModelPreviewGeometryReader.TryLoad(data, out var previewGeometry, out var reason)
            ? CreateGeometryEntry(previewGeometry, touchedUtc)
            : CreateDecodeFailedEntry(normalizedPath, reason, touchedUtc);
    }

    private CacheEntry CreateDecodeFailedEntry(string normalizedPath, string? reason, DateTime touchedUtc)
    {
        _logger.LogDebug(
            "object selection geometry decode failed for {ModelPath}: {Reason}",
            normalizedPath,
            reason ?? "unknown reason");
        return new CacheEntry
        {
            LastTouchedUtc = touchedUtc,
        };
    }

    private static CacheEntry CreateGeometryEntry(ModelPreviewGeometryReader.PreviewGeometry previewGeometry, DateTime touchedUtc)
        => new()
        {
            Geometry = new SceneSelectionGeometry(
                previewGeometry.Positions,
                previewGeometry.Indices),
            LastTouchedUtc = touchedUtc,
        };

    private void TrimExpiredUnsafe(DateTime nowUtc)
    {
        if (_cache.Count == 0)
        {
            return;
        }

        foreach (var (path, entry) in _cache.ToArray())
        {
            if (nowUtc - entry.LastTouchedUtc >= EntryTtl)
            {
                _cache.Remove(path);
            }
        }
    }
}
