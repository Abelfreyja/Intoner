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
    private const int MaxEntries = 256;
    private const long MaxGeometryBytes = 192L * 1024 * 1024;
    private static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TrimInterval = TimeSpan.FromSeconds(30);

    private sealed class CacheEntry(Task<SceneSelectionGeometry?> geometry)
    {
        public Task<SceneSelectionGeometry?> Geometry { get; } = geometry;
        public Task<bool>? Acceleration { get; set; }
        public bool GeometrySizeChecked { get; set; }
        public bool IsPending => !Geometry.IsCompleted || Acceleration?.IsCompleted == false;
        public long EstimatedBytes => Geometry.IsCompletedSuccessfully ? Geometry.Result?.EstimatedBytes ?? 0 : 0;
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;
        public DateTime LastTouchedUtc { get; set; } = DateTime.UtcNow;
    }

    private readonly ILogger<ObjectModelSelectionGeometryCache> _logger;
    private readonly IDataManager _gameData;
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentExclusiveSchedulerPair _buildScheduler = new(TaskScheduler.Default, maxConcurrencyLevel: 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly CancellationToken _disposeToken;
    private DateTime _nextTrimUtc;
    private bool _disposed;

    public ObjectModelSelectionGeometryCache(ILogger<ObjectModelSelectionGeometryCache> logger, IDataManager gameData)
    {
        _logger = logger;
        _gameData = gameData;
        _disposeToken = _disposeCancellation.Token;
    }

    public bool TryGetGeometry(string modelPath, out SceneSelectionGeometry geometry)
        => TryGetGeometryCore(modelPath, false, out geometry);

    public bool TryGetRaycastGeometry(string modelPath, out SceneSelectionGeometry geometry)
        => TryGetGeometryCore(modelPath, true, out geometry);

    public SceneGeometryAvailability RequestRaycastGeometry(string modelPath, out SceneSelectionGeometry geometry)
    {
        geometry = null!;
        if (!ObjectResourcePathUtility.TryNormalizeTrackedPath(modelPath, out string normalizedPath))
        {
            return SceneGeometryAvailability.Unavailable;
        }

        CacheEntry? entry;
        lock (_cacheLock)
        {
            if (_disposed)
            {
                return SceneGeometryAvailability.Unavailable;
            }

            if (!_cache.ContainsKey(normalizedPath) && _cache.Values.Any(static item => item.IsPending))
            {
                return SceneGeometryAvailability.Loading;
            }

            entry = GetEntry(normalizedPath, true);
        }

        if (entry is null)
        {
            return SceneGeometryAvailability.Unavailable;
        }

        if (!entry.Geometry.IsCompleted || entry.Acceleration?.IsCompleted != true)
        {
            return SceneGeometryAvailability.Loading;
        }

        if (!entry.Geometry.IsCompletedSuccessfully || !entry.Acceleration.IsCompletedSuccessfully)
        {
            return SceneGeometryAvailability.Unavailable;
        }

        geometry = entry.Geometry.Result!;
        return geometry is not null && entry.Acceleration.Result
            ? SceneGeometryAvailability.Ready
            : SceneGeometryAvailability.Unavailable;
    }

    private bool TryGetGeometryCore(string modelPath, bool requestAcceleration, out SceneSelectionGeometry geometry)
    {
        geometry = null!;
        if (!ObjectResourcePathUtility.TryNormalizeTrackedPath(modelPath, out string normalizedPath))
        {
            return false;
        }

        CacheEntry? entry;
        lock (_cacheLock)
        {
            entry = GetEntry(normalizedPath, requestAcceleration);
        }

        if (entry is null)
        {
            return false;
        }

        try
        {
            geometry = entry.Geometry.GetAwaiter().GetResult()!;
            if (!entry.GeometrySizeChecked)
            {
                lock (_cacheLock)
                {
                    entry.GeometrySizeChecked = true;
                    TrimToBudget(entry);
                }
            }

            return geometry is not null;
        }
        catch (OperationCanceledException) when (_disposeToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private CacheEntry? GetEntry(string normalizedPath, bool requestAcceleration)
    {
        if (_disposed)
        {
            return null;
        }

        DateTime now = DateTime.UtcNow;
        TrimExpired(now);

        if (_cache.TryGetValue(normalizedPath, out CacheEntry? entry)
            && entry.Geometry.IsCompletedSuccessfully && entry.Geometry.Result is null
            && now - entry.CreatedUtc >= ObjectAssetFileUtility.AssetReadRetryDelay)
        {
            _cache.Remove(normalizedPath);
            entry = null;
        }

        if (entry is null)
        {
            entry = new CacheEntry(Task.Factory.StartNew(() => LoadGeometry(normalizedPath),
                _disposeToken, TaskCreationOptions.DenyChildAttach, _buildScheduler.ConcurrentScheduler));
            _cache.Add(normalizedPath, entry);
            TrimToBudget(entry);
        }

        entry.LastTouchedUtc = now;

        if (requestAcceleration)
        {
            entry.Acceleration ??= BuildAccelerationAsync(entry.Geometry, normalizedPath);
        }

        return entry;
    }

    private void TrimToBudget(CacheEntry? keep = null)
    {
        long bytes = 0;
        foreach (CacheEntry entry in _cache.Values)
        {
            bytes += entry.EstimatedBytes;
        }

        if (_cache.Count <= MaxEntries && bytes <= MaxGeometryBytes)
        {
            return;
        }

        foreach (var candidate in _cache.Where(pair => !pair.Value.IsPending && !ReferenceEquals(pair.Value, keep)).OrderBy(static pair => pair.Value.LastTouchedUtc).ToArray())
        {
            _cache.Remove(candidate.Key);
            bytes -= candidate.Value.EstimatedBytes;
            if (_cache.Count <= MaxEntries && bytes <= MaxGeometryBytes)
            {
                break;
            }
        }
    }

    public void Touch(string modelPath)
    {
        if (!ObjectResourcePathUtility.TryNormalizeTrackedPath(modelPath, out string normalizedPath))
        {
            return;
        }

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(normalizedPath, out CacheEntry? entry))
            {
                entry.LastTouchedUtc = DateTime.UtcNow;
            }
        }
    }

    private async Task<bool> BuildAccelerationAsync(Task<SceneSelectionGeometry?> load, string modelPath)
    {
        try
        {
            SceneSelectionGeometry? geometry = await load.ConfigureAwait(false);

            if (geometry is null)
            {
                return false;
            }

            Task build;
            lock (_cacheLock)
            {
                _disposeToken.ThrowIfCancellationRequested();

                if (_disposed)
                {
                    return false;
                }

                build = Task.Factory.StartNew(() => geometry.BuildAcceleration(_disposeToken),
                    _disposeToken, TaskCreationOptions.DenyChildAttach, _buildScheduler.ConcurrentScheduler);
            }

            await build.ConfigureAwait(false);
            lock (_cacheLock)
            {
                TrimToBudget();
            }

            return true;
        }
        catch (OperationCanceledException) when (_disposeToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "object selection geometry acceleration failed for {ModelPath}", modelPath);
            return false;
        }
    }

    private SceneSelectionGeometry? LoadGeometry(string modelPath)
    {
        try
        {
            _disposeToken.ThrowIfCancellationRequested();
            byte[]? data = ObjectLocalFilePathUtility.IsLocalFilePath(modelPath)
                ? ObjectAssetFileUtility.TryReadLocalFileBytes(modelPath, _disposeToken)
                : _gameData.GetFile(modelPath)?.Data;
            _disposeToken.ThrowIfCancellationRequested();

            if (data is null)
            {
                _logger.LogDebug("object selection geometry missing file {ModelPath}", modelPath);
                return null;
            }

            if (!ModelPreviewGeometryReader.TryLoad(data, out var previewGeometry, out string? reason))
            {
                _logger.LogDebug("object selection geometry decode failed for {ModelPath}: {Reason}", modelPath, reason);
                return null;
            }

            _disposeToken.ThrowIfCancellationRequested();
            return new SceneSelectionGeometry(previewGeometry.Positions, previewGeometry.Indices);
        }
        catch (OperationCanceledException) when (_disposeToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "object selection geometry load failed for {ModelPath}", modelPath);
            return null;
        }
    }

    private void TrimExpired(DateTime now)
    {
        if (now < _nextTrimUtc)
        {
            return;
        }

        _nextTrimUtc = now + TrimInterval;

        foreach (string path in _cache.Where(pair => pair.Value.Geometry.IsCompleted
                     && pair.Value.Acceleration?.IsCompleted != false
                     && now - pair.Value.LastTouchedUtc >= EntryTtl).Select(pair => pair.Key).ToArray())
        {
            _cache.Remove(path);
        }
    }

    public void Dispose()
    {
        lock (_cacheLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cache.Clear();
            _buildScheduler.Complete();
        }

        _disposeCancellation.Cancel();
        _buildScheduler.Completion.Wait(BuildShutdownWaitMilliseconds, CancellationToken.None);
        _disposeCancellation.Dispose();
    }
}
